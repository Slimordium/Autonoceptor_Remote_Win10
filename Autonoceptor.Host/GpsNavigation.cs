using System;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autonoceptor.Shared.Imu;
using Autonoceptor.Shared.Utilities;
using Nito.AsyncEx;
using NLog;

namespace Autonoceptor.Host
{
    public class GpsNavigation : LidarNavOverride
    {
        private readonly ILogger _logger = LogManager.GetCurrentClassLogger();

        private IDisposable _gpsNavDisposable;
        private IDisposable _gpsNavSwitchDisposable;
        private IDisposable _odometerDisposable;
        private IDisposable _speedControllerDisposable;
        private IDisposable _gpsHeadingUpdateDisposable;
        private IDisposable _imuHeadingUpdateDisposable;
        private IDisposable _steeringUpdater;

        private readonly AsyncLock _asyncLock = new AsyncLock();

        public WaypointList Waypoints { get; set; } = new WaypointList();

        public GpsNavParameters GpsNavParameters { get; set; } = new GpsNavParameters();

        public int CurrentWaypointIndex { get; set; }

        protected GpsNavigation(CancellationTokenSource cancellationTokenSource, string brokerHostnameOrIp)
            : base(cancellationTokenSource, brokerHostnameOrIp)
        {
            cancellationTokenSource.Token.Register(async () => { await WaypointFollowEnable(false); });
        }

        private bool _followingWaypoints;
        protected async Task<bool> GetFollowingWaypoints()
        {
            using (await _asyncLock.LockAsync())
            {
                return _followingWaypoints;
            }
        }

        protected async Task SetFollowingWaypoints(bool isFollowing)
        {
            using (await _asyncLock.LockAsync())
            {
                _followingWaypoints = isFollowing;
            }
        }

        protected new async Task InitializeAsync()
        {
            await base.InitializeAsync();

            _gpsNavSwitchDisposable = PwmController.GetObservable()
                .Where(channel => channel.ChannelId == GpsNavEnabledChannel)
                .ObserveOnDispatcher()
                .Subscribe(async channelData => { await WaypointFollowEnable(channelData.DigitalValue); });

            _steeringUpdater = Observable
                .Interval(TimeSpan.FromMilliseconds(200))
                .ObserveOnDispatcher()
                .Subscribe(async _ =>
                {
                    if (!await GetFollowingWaypoints())
                        return;

                    await SetVehicleHeading(
                        await GpsNavParameters.GetSteeringDirection(), 
                        await GpsNavParameters.GetSteeringMagnitude());
                });

            _gpsHeadingUpdateDisposable = Gps
                .GetObservable()
                .ObserveOnDispatcher()
                .Subscribe(async gpsFixData =>
                {
                    await GpsNavParameters.SetCurrentHeading(gpsFixData.Heading);
                });

            _imuHeadingUpdateDisposable = Imu
                .GetReadObservable()
                .ObserveOnDispatcher()
                .Subscribe(async imuData =>
                {
                    await GpsNavParameters.SetCurrentHeading(imuData.Yaw);
                });
        }

        public async Task WaypointFollowEnable(bool enabled)
        {
            if (await GetFollowingWaypoints() == enabled)
                return;

            await SetFollowingWaypoints(enabled);

            if (enabled)
            {
                await Lcd.WriteAsync("Started Nav to", 1);
                await Lcd.WriteAsync($"{Waypoints.Count} WPs", 2);

                _gpsNavDisposable?.Dispose();
                _odometerDisposable?.Dispose();

                _gpsNavDisposable = Gps
                    .GetObservable()
                    .ObserveOnDispatcher()
                    .Subscribe(async fix =>
                    {
                        await SetMoveTargets();
                    });

                _speedControllerDisposable = Observable
                    .Interval(TimeSpan.FromMilliseconds(100))
                    .ObserveOnDispatcher()
                    .Subscribe(
                        async _ =>
                        {
                            var ppi = await GpsNavParameters.GetTargetPpi();
                            var moveMagnitude = await GpsNavParameters.GetLastMoveMagnitude();

                            if (ppi < 2 || !await GetFollowingWaypoints())
                            {
                                await GpsNavParameters.SetTargetPpi(0);

                                await EmergencyBrake();
                                return;
                            }
                            
                            var odometer = await Odometer.GetOdometerData();

                            //Give it some wiggle room
                            if (odometer.PulseCount < ppi + 50 && odometer.PulseCount > ppi - 50)
                            {
                                return;
                            }

                            if (odometer.PulseCount < ppi)
                                moveMagnitude = moveMagnitude + 2;

                            if (odometer.PulseCount > ppi)
                                moveMagnitude = moveMagnitude - 1;

                            if (moveMagnitude > 28) //Perhaps .. use pitch accounted for?
                                moveMagnitude = 28;

                            if (moveMagnitude < 0)
                                moveMagnitude = 0;

                            await SetVehicleTorque(MovementDirection.Forward, moveMagnitude);

                            await GpsNavParameters.SetLastMoveMagnitude(moveMagnitude);
                        });

                return;
            }

            await Lcd.WriteAsync("WP Follow finished");
            _logger.Log(LogLevel.Info, "WP Follow finished");

            CurrentWaypointIndex = 0;

            //Cleanup WP Nav specific resources 
            _gpsNavDisposable?.Dispose();
            _odometerDisposable?.Dispose();
            _speedControllerDisposable?.Dispose();

            _gpsNavDisposable = null;
            _odometerDisposable = null;
            _speedControllerDisposable = null;
            //-----------------------------------

            await EmergencyBrake();
        }

        private async Task<bool> CheckWaypointFollowFinished()
        {
            if (CurrentWaypointIndex <= Waypoints.Count && await GetFollowingWaypoints())
                return false;

            _logger.Log(LogLevel.Info, $"Nav finished {Waypoints.Count} WPs");

            await WaypointFollowEnable(false);
            return true;
        }

        public async Task SyncImuYaw()
        {
            var uncorrectedYaw = (await Imu.Get()).UncorrectedYaw;
            var diff = uncorrectedYaw - await GpsNavParameters.GetCurrentHeading();

            ImuData.YawCorrection = diff;

            var yaw = (await Imu.Get()).Yaw;

            _logger.Log(LogLevel.Info, $"IMU Yaw correction: {diff}, Corrected Yaw: {yaw}");
        }

        public async Task SyncImuYaw(double heading)
        {
            var uncorrectedYaw = (await Imu.Get()).UncorrectedYaw;
            var diff = uncorrectedYaw - heading;

            ImuData.YawCorrection = diff;

            var yaw = (await Imu.Get()).Yaw;

            _logger.Log(LogLevel.Info, $"IMU Yaw correction: {diff}, Corrected Yaw: {yaw}");
        }

        //GPS Heading seems almost useless. Using Yaw instead. OK... Set GPS to "Pedestrian nav mode" heading now seems decent...
        public async Task SetMoveTargets()
        {
            var moveReq = new MoveRequest(MoveRequestType.Gps);

            if (await CheckWaypointFollowFinished())
                return;

            var currentWp = Waypoints[CurrentWaypointIndex];

            var gpsFixData = await Gps.Get();

            var distanceAndHeading = GpsExtensions.GetDistanceAndHeadingToDestination(gpsFixData.Lat, gpsFixData.Lon, currentWp.GpsFixData.Lat, currentWp.GpsFixData.Lon);
            moveReq.Distance = distanceAndHeading[0];

            var headingToWaypoint = distanceAndHeading[1];

            await SyncImuYaw(gpsFixData.Heading);

            await GpsNavParameters.SetTargetHeading(headingToWaypoint);

            if (distanceAndHeading[0] < Waypoints[CurrentWaypointIndex].Radius)
            {
                if (Waypoints[CurrentWaypointIndex].Behaviour == WaypointType.Continue)
                {
                    CurrentWaypointIndex++;
                    return;
                }
                else if (Waypoints[CurrentWaypointIndex].Behaviour == WaypointType.Pause)
                {
                    await WaypointFollowEnable(false);
                    CurrentWaypointIndex++;
                    await Task.Delay(1000);
                    await WaypointFollowEnable(true);
                }
                else
                {
                    await WaypointFollowEnable(false);

                    await CheckWaypointFollowFinished();
                }
                
            }
            else
            {
                _logger.Log(LogLevel.Trace, $"Current Heading: {gpsFixData.Heading}, Heading to WP: {headingToWaypoint}");
                _logger.Log(LogLevel.Trace, $"GPS Distance to WP: {moveReq.Distance}in, {moveReq.Distance / 12}ft");

                await GpsNavParameters.SetTargetPpi(460);//This is a pretty even pace, the GPS can keep up with it ok
            }
        }

        private async Task SetVehicleHeading(SteeringDirection direction, double magnitude)
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

        private async Task SetVehicleTorque(MovementDirection direction, double magnitude)
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
    }
}