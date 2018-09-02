using System;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autonoceptor.Service.Hardware;
using Autonoceptor.Shared.Gps;
using Autonoceptor.Shared.Imu;
using Autonoceptor.Shared.Utilities;
using NLog;

namespace Autonoceptor.Host
{
    public class GpsNavigation : LidarNavOverride
    {
        private readonly ILogger _logger = LogManager.GetCurrentClassLogger();

        private double _distanceToNextWaypoint;

        private bool _followingWaypoints;

        private IDisposable _gpsNavDisposable;

        private IDisposable _gpsNavSwitchDisposable;

        private double _lastMoveMagnitude;
        private IDisposable _odometerDisposable;

        private int _requestedPpInterval;

        private IDisposable _speedControllerDisposable;

        private OdometerData _startOdometerData;

        private IDisposable _steerMagnitudeDecayDisposable;

        private double _targetHeading;

        protected GpsNavigation(CancellationTokenSource cancellationTokenSource, string brokerHostnameOrIp)
            : base(cancellationTokenSource, brokerHostnameOrIp)
        {
            cancellationTokenSource.Token.Register(async () => { await WaypointFollowEnable(false); });
        }

        public int CurrentWaypointIndex { get; set; }

        protected bool FollowingWaypoints
        {
            get => Volatile.Read(ref _followingWaypoints);
            set => Volatile.Write(ref _followingWaypoints, value);
        }

        public WaypointList Waypoints { get; set; } = new WaypointList();

        protected new async Task InitializeAsync()
        {
            await base.InitializeAsync();

            _gpsNavSwitchDisposable = PwmController.GetObservable()
                .Where(channel => channel.ChannelId == GpsNavEnabledChannel)
                .ObserveOnDispatcher()
                .Subscribe(async channelData => { await WaypointFollowEnable(channelData.DigitalValue); });
        }

        public async Task WaypointFollowEnable(bool enabled)
        {
            if (FollowingWaypoints == enabled)
                return;

            FollowingWaypoints = enabled;

            if (enabled)
            {
                await Lcd.WriteAsync("Started Nav to", 1);
                await Lcd.WriteAsync($"{Waypoints.Count} WPs", 2);

                _steerMagnitudeDecayDisposable?.Dispose();
                _gpsNavDisposable?.Dispose();
                _odometerDisposable?.Dispose();

                _gpsNavDisposable = Gps
                    .GetObservable()
                    .ObserveOnDispatcher()
                    .Subscribe(async fix =>
                    {
                        await UpdateMoveRequest(fix);

                        var currentLocation = await Gps.Get();

                        var wp = Waypoints[CurrentWaypointIndex].GpsFixData;

                        var distHead = GpsExtensions.GetDistanceAndHeadingToDestination(currentLocation.Lat, currentLocation.Lon, wp.Lat, wp.Lon);

                        if (distHead[0] < 30)
                        {
                            if (Waypoints.Count > CurrentWaypointIndex + 1)
                            {
                                CurrentWaypointIndex++;
                                return;
                            }

                            await WaypointFollowEnable(false);

                            _distanceToNextWaypoint = 0;

                            _startOdometerData = null;

                            await CheckWaypointFollowFinished();
                        }

                    });

                //_odometerDisposable = Odometer
                //    .GetObservable()
                //    .ObserveOnDispatcher()
                //    .Subscribe(async odometerData =>
                //    {
                //        if (Math.Abs(_distanceToNextWaypoint) < 0 || _startOdometerData == null)
                //            return;

                //        var traveledInches = odometerData.InTraveled - _startOdometerData.InTraveled;

                //        //We made it, yay.
                //        if (traveledInches >= _distanceToNextWaypoint + 34)
                //        {
                //            if (Waypoints.Count > CurrentWaypointIndex + 1)
                //            {
                //                CurrentWaypointIndex++;
                //                return;
                //            }

                //            await WaypointFollowEnable(false);

                //            _distanceToNextWaypoint = 0;

                //            _startOdometerData = null;

                //            await CheckWaypointFollowFinished();
                //        }
                //    });

                _speedControllerDisposable = Observable
                    .Interval(TimeSpan.FromMilliseconds(100))
                    .ObserveOnDispatcher()
                    .Subscribe(
                        async _ =>
                        {
                            var ppi = Volatile.Read(ref _requestedPpInterval);
                            double mm = Volatile.Read(ref _lastMoveMagnitude);

                            if (ppi < 2 || !FollowingWaypoints)
                            {
                                await EmergencyBrake();
                                return;
                            }
                            
                            var odometer = await Odometer.GetOdometerData();

                            if (odometer.PulseCount < ppi)
                                mm = mm + 2;

                            if (odometer.PulseCount > ppi)
                                mm = mm - 1;

                            if (mm > 28) //Perhaps .. use pitch accounted for?
                                mm = 28;

                            if (mm < 0)
                                mm = 0;

                            await Move(MovementDirection.Forward, mm);

                            Volatile.Write(ref _lastMoveMagnitude, mm);
                        });

                return;
            }

            await EmergencyBrake();

            _startOdometerData = null;

            await Lcd.WriteAsync("WP Follow finished");
            _logger.Log(LogLevel.Info, "WP Follow finished");

            CurrentWaypointIndex = 0;

            _distanceToNextWaypoint = 0;

            _steerMagnitudeDecayDisposable?.Dispose();
            _gpsNavDisposable?.Dispose();
            _odometerDisposable?.Dispose();
            _speedControllerDisposable?.Dispose();

            _steerMagnitudeDecayDisposable = null;
            _gpsNavDisposable = null;
            _odometerDisposable = null;
            _speedControllerDisposable = null;
        }

        private async Task<bool> CheckWaypointFollowFinished()
        {
            if (CurrentWaypointIndex <= Waypoints.Count && FollowingWaypoints)
                return false;

            _logger.Log(LogLevel.Info, $"Nav finished {Waypoints.Count} WPs");

            await WaypointFollowEnable(false);
            return true;
        }

        private async Task UpdateMoveRequest(GpsFixData gpsFixData)
        {
            if (await CheckWaypointFollowFinished())
                return;

            try
            {
                var currentWp = Waypoints[CurrentWaypointIndex];

                var moveReq = await GetMoveRequest(currentWp.GpsFixData, gpsFixData);

                await WriteToHardware(moveReq);

                await Lcd.WriteAsync($"{moveReq.SteeringDirection} {moveReq.SteeringMagnitude}", 1);
                await Lcd.WriteAsync($"Dist {moveReq.Distance} {CurrentWaypointIndex}", 2);
            }
            catch (Exception e)
            {
                _logger.Log(LogLevel.Error, e.Message);

                await WaypointFollowEnable(false);
            }
        }

        public async Task SetImuYawToNorth()
        {
            var uncorrectedYaw = (await Imu.Get()).UncorrectedYaw;

            ImuData.YawCorrection = uncorrectedYaw;

            var yaw = (await Imu.Get()).Yaw;

            _logger.Log(LogLevel.Info, $"IMU Yaw correction: {uncorrectedYaw}, Corrected Yaw: {yaw}");
        }

        private SteeringDirection GetSteerDirection(double diff)
        {
            SteeringDirection steerDirection;

            if (diff < 0)
            {
                steerDirection = Math.Abs(diff) > 180 ? SteeringDirection.Left : SteeringDirection.Right;
            }
            else
            {
                steerDirection = Math.Abs(diff) > 180 ? SteeringDirection.Right : SteeringDirection.Left;
            }

            return steerDirection;
        }

        private double GetSteeringMagnitude(double diff)
        {
            var absDiff = Math.Abs(diff);

            if (absDiff > 25) //Can turn about 45 degrees 
                absDiff = 25;

            return absDiff;
        }

        //GPS Heading seems almost useless. Using Yaw instead. OK... Set GPS to "Pedestrian nav mode" heading now seems decent...
        public async Task<MoveRequest> GetMoveRequest(GpsFixData waypoint, GpsFixData currentLocation)
        {
            var moveReq = new MoveRequest(MoveRequestType.Gps);

            //var currentYaw = (await Imu.Get()).Yaw;
            var currentYaw = currentLocation.Heading;

            var distanceAndHeading = GpsExtensions.GetDistanceAndHeadingToDestination(currentLocation.Lat, currentLocation.Lon, waypoint.Lat, waypoint.Lon);
            moveReq.Distance = distanceAndHeading[0];

            var headingToWaypoint = distanceAndHeading[1];

            if (_startOdometerData == null)
            {
                _startOdometerData = await Odometer.GetOdometerData();
                _distanceToNextWaypoint = moveReq.Distance;
            }

            if (Math.Abs(moveReq.Distance) < 3)
            {
                _logger.Log(LogLevel.Info, "Less than 3in to WP, ignoring");
                return moveReq;
            }

            var headingDifference = currentYaw - headingToWaypoint;

            _logger.Log(LogLevel.Trace, $"Current Heading: {currentYaw}, Heading to WP: {headingToWaypoint}");
            _logger.Log(LogLevel.Trace, $"GPS Distance to WP: {moveReq.Distance}in, {moveReq.Distance / 12}ft");

            _targetHeading = headingToWaypoint;

            moveReq.SteeringDirection = GetSteerDirection(headingDifference);

            moveReq.SteeringMagnitude = GetSteeringMagnitude(headingDifference);
            
            Volatile.Write(ref _requestedPpInterval, 460);//This is a pretty even pace, the GPS can keep up with it ok

            await Turn(moveReq.SteeringDirection, moveReq.SteeringMagnitude);

            return moveReq;
        }

        private async Task Turn(SteeringDirection direction, double magnitude)
        {
            var steerValue = CenterPwm * 4;

            switch (direction)
            {
                case SteeringDirection.Left:
                    steerValue = Convert.ToUInt16(magnitude.Map(0, 65, CenterPwm, LeftPwmMax)) * 4;
                    break;
                case SteeringDirection.Right:
                    steerValue = Convert.ToUInt16(magnitude.Map(0, 65, CenterPwm, RightPwmMax)) * 4;
                    break;
            }

            await PwmController.SetChannelValue(steerValue, SteeringChannel);
        }

        private async Task Move(MovementDirection direction, double magnitude)
        {
            var moveValue = StoppedPwm * 4;

            switch (direction)
            {
                case MovementDirection.Forward:
                    moveValue = Convert.ToUInt16(magnitude.Map(0, 45, StoppedPwm, ForwardPwmMax)) * 4;
                    break;
                case MovementDirection.Reverse:
                    moveValue = Convert.ToUInt16(magnitude.Map(0, 45, StoppedPwm, ReversePwmMax)) * 4;
                    break;
            }

            await PwmController.SetChannelValue(moveValue, MovementChannel);
        }

        private async Task WriteToHardware(MoveRequest request)
        {
            var moveValue = StoppedPwm * 4;
            var steerValue = CenterPwm * 4;

            if (request.SteeringMagnitude > 45)
                request.SteeringMagnitude = 45;

            switch (request.SteeringDirection)
            {
                case SteeringDirection.Left:
                    steerValue = Convert.ToUInt16(request.SteeringMagnitude.Map(0, 45, CenterPwm, LeftPwmMax)) * 4;
                    break;
                case SteeringDirection.Right:
                    steerValue = Convert.ToUInt16(request.SteeringMagnitude.Map(0, 45, CenterPwm, RightPwmMax)) * 4;
                    break;
            }

            await PwmController.SetChannelValue(steerValue, SteeringChannel);

            switch (request.MovementDirection)
            {
                case MovementDirection.Forward:
                    moveValue = Convert.ToUInt16(request.MovementMagnitude.Map(0, 45, StoppedPwm, ForwardPwmMax)) * 4;
                    break;
                case MovementDirection.Reverse:
                    moveValue = Convert.ToUInt16(request.MovementMagnitude.Map(0, 45, StoppedPwm, ReversePwmMax)) * 4;
                    break;
            }

            await PwmController.SetChannelValue(moveValue, MovementChannel);
        }
    }
}