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

        public int CurrentWaypointIndex { get; set; }

        private bool _followingWaypoints;
        protected bool FollowingWaypoints
        {
            get => Volatile.Read(ref _followingWaypoints);
            set => Volatile.Write(ref _followingWaypoints, value);
        }

        public WaypointList Waypoints { get; set; } = new WaypointList();

        private IDisposable _gpsNavDisposable;
        private IDisposable _gpsNavSwitchDisposable;
        private IDisposable _odometerDisposable;

        private double _gpsNavMoveMagnitude = 21;
        public double GpsNavMoveMagnitude
        {
            get => Volatile.Read(ref _gpsNavMoveMagnitude);
            set => Volatile.Write(ref _gpsNavMoveMagnitude, value);
        }

        private IDisposable _steerMagnitudeDecayDisposable;

        private MoveRequest _currentGpsMoveRequest;

        protected MoveRequest CurrentGpsMoveRequest
        {
            get => Volatile.Read(ref _currentGpsMoveRequest);
            private set => Volatile.Write(ref _currentGpsMoveRequest, value);
        }

        protected GpsNavigation(CancellationTokenSource cancellationTokenSource, string brokerHostnameOrIp) 
            : base(cancellationTokenSource, brokerHostnameOrIp)
        {
            cancellationTokenSource.Token.Register(async () =>
            {
                await WaypointFollowEnable(false);
            });
        }

        protected new async Task InitializeAsync()
        {
            await base.InitializeAsync();

            _gpsNavSwitchDisposable = PwmController.GetObservable()
                .Where(channel => channel.ChannelId == GpsNavEnabledChannel)
                .ObserveOnDispatcher()
                .Subscribe(async channelData =>
                {
                    await WaypointFollowEnable(channelData.DigitalValue);
                });

           
        }

        private int _lastMoveMagnitude;

        private int _requestedPpInterval;


        public async Task WaypointFollowEnable(bool enabled)
        {
            if (FollowingWaypoints == enabled)
                return;

            FollowingWaypoints = enabled;

            if (enabled)
            {
                await Lcd.WriteAsync($"Started Nav to", 1);
                await Lcd.WriteAsync($"{Waypoints.Count} WPs", 2);

                _steerMagnitudeDecayDisposable?.Dispose();
                _gpsNavDisposable?.Dispose();
                _odometerDisposable?.Dispose();

                _gpsNavDisposable = Gps.GetObservable()
                    .ObserveOnDispatcher()
                    .Subscribe(async fix =>
                    {
                        await UpdateMoveRequest(fix);
                    });


                _odometerDisposable = Odometer.GetObservable()
                    .ObserveOnDispatcher()
                    .Subscribe(async odometerData =>
                    {
                        if (Math.Abs(_distanceToNextWaypoint) < 0 || _startOdometerData == null)
                            return;

                        var traveledInches = odometerData.InTraveled - _startOdometerData.InTraveled;

                        var headingDifference = (await RazorImu.Get()).Yaw - _targetHeading;

                        await Turn(GetSteerDirection(headingDifference), GetSteeringMagnitude(headingDifference));


                        //We made it, yay.
                        if (traveledInches >= _distanceToNextWaypoint - 25)
                        {
                            CurrentWaypointIndex++;

                            _distanceToNextWaypoint = 0;

                            _startOdometerData = null;

                            _requestedPpInterval = 0;

                            await CheckWaypointFollowFinished();
                        }

                    });

                _speedControllerDisposable = Observable
                    .Interval(TimeSpan.FromMilliseconds(100))
                    .ObserveOnDispatcher()
                    .Subscribe(
                        async _ =>
                        {
                            if (_requestedPpInterval < 2 || Stopped)
                            {
                                await EmergencyBrake();
                                return;
                            }

                            await EmergencyBrake(true);

                            var odometer = await Odometer.GetOdometerData();

                            if (odometer.PulseCount < _requestedPpInterval)
                            {
                                _lastMoveMagnitude = _lastMoveMagnitude + 2;
                            }

                            if (odometer.PulseCount > _requestedPpInterval)
                            {
                                _lastMoveMagnitude = _lastMoveMagnitude - 2;
                            }

                            if (_lastMoveMagnitude > 20)
                                _lastMoveMagnitude = 20;

                            if (_lastMoveMagnitude < 0)
                                _lastMoveMagnitude = 0;

                            await Move(MovementDirection.Forward, _lastMoveMagnitude);
                        });

                return;
            }

            await EmergencyBrake(true);

            await Lcd.WriteAsync("GPS Nav stopped");

            _steerMagnitudeDecayDisposable?.Dispose();
            _gpsNavDisposable?.Dispose();
            _odometerDisposable?.Dispose();
            _speedControllerDisposable?.Dispose();

            _steerMagnitudeDecayDisposable = null;
            _gpsNavDisposable = null;
            _odometerDisposable = null;
            _speedControllerDisposable = null;
        }

        private double _distanceToNextWaypoint;

        private OdometerData _startOdometerData;

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
            {
                return;
            }

            var currentWp = Waypoints[CurrentWaypointIndex];

            var moveReq = await GetMoveRequest(currentWp.GpsFixData, gpsFixData);

            moveReq.MovementMagnitude = GpsNavMoveMagnitude;

            await WriteToHardware(moveReq);

            await Lcd.WriteAsync($"{moveReq.SteeringDirection} {moveReq.SteeringMagnitude}", 1);
            await Lcd.WriteAsync($"Dist {moveReq.Distance} {CurrentWaypointIndex}", 2);

            //This is wrong....
            //if (moveReq.Distance <= 36) //This should probably be slightly larger than the turning radius?
            //{
            //    CurrentWaypointIndex++;
            //}
        }

        public async Task SyncImuToGpsHeading()
        {
            var gpsHeading = (await Gps.Get()).Heading;
            var imuHeading = (await RazorImu.Get()).UncorrectedYaw;

            var diff = imuHeading - gpsHeading;

            ImuData.YawCorrection = Math.Abs(diff);

            imuHeading = (await RazorImu.Get()).Yaw;

            _logger.Log(LogLevel.Info, $"IMU Yaw correction: {diff}, Corrected Yaw: {imuHeading}");

            
        }

        private IDisposable _speedControllerDisposable;

        private SteeringDirection GetSteerDirection(double diff)
        {
            var dir = SteeringDirection.Center;

            if (diff < 0)
            {
                if (Math.Abs(diff) > 180)
                {
                    dir = SteeringDirection.Left;
                }
                else
                {
                    dir = SteeringDirection.Right;
                }
            }
            else
            {
                if (Math.Abs(diff) > 180)
                {
                    dir = SteeringDirection.Right;
                }
                else
                {
                    dir = SteeringDirection.Left;
                }
            }

            return dir;
        }

        private double GetSteeringMagnitude(double diff)
        {
            var absDiff = Math.Abs(diff);

            if (absDiff > 45) //Can turn about 45 degrees 
                absDiff = 45;

            return absDiff;
        }

        public async Task<MoveRequest> GetMoveRequest(GpsFixData waypoint, GpsFixData currentLocation)
        {
            var moveReq = new MoveRequest(MoveRequestType.Gps);

            var distanceAndHeading = GpsExtensions.GetDistanceAndHeadingToDestination(currentLocation.Lat, currentLocation.Lon, waypoint.Lat, waypoint.Lon);
            moveReq.Distance = distanceAndHeading[0];

            var headingToWaypoint = distanceAndHeading[1];

            if (_startOdometerData == null)
            {
                _startOdometerData = await Odometer.GetOdometerData();
                _distanceToNextWaypoint = moveReq.Distance;
            }

            if (Math.Abs(moveReq.Distance) < 6)
                return moveReq;

            var diff = currentLocation.Heading - headingToWaypoint;

            _logger.Log(LogLevel.Trace, $"Current Heading: {currentLocation.Heading}, Heading to WP: {headingToWaypoint}");
            _logger.Log(LogLevel.Trace, $"GPS Distance to WP: {moveReq.Distance}in");

            _targetHeading = headingToWaypoint;

            moveReq.SteeringDirection = GetSteerDirection(diff);

            moveReq.SteeringMagnitude = GetSteeringMagnitude(diff);

            _requestedPpInterval = 400;//This is a pretty even pace, the GPS can keep up with it ok

            await Turn(moveReq.SteeringDirection, moveReq.SteeringMagnitude);

            return moveReq;
        }

        private double _targetHeading;


        private async Task Turn(SteeringDirection direction, double magnitude)
        {
            var steerValue = CenterPwm * 4;

            if (magnitude > 100)
                magnitude = 100;

            switch (direction)
            {
                case SteeringDirection.Left:
                    steerValue = Convert.ToUInt16(magnitude.Map(0, 45, CenterPwm, LeftPwmMax)) * 4;
                    break;
                case SteeringDirection.Right:
                    steerValue = Convert.ToUInt16(magnitude.Map(0, 45, CenterPwm, RightPwmMax)) * 4;
                    break;
            }

            await PwmController.SetChannelValue(steerValue, SteeringChannel);
        }

        //TODO: Set speed as percentage, control PWM value off of odometer pulse count, this will set ground speed instead of guessing
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
            CurrentGpsMoveRequest = request;

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