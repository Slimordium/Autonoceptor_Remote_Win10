using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using Autonoceptor.Shared.Gps;
using Autonoceptor.Shared.Imu;
using Autonoceptor.Shared.Utilities;
using Nito.AsyncEx;
using NLog;

namespace Autonoceptor.Host
{
    public class GpsNavigation : LidarNavOverride
    {
        private readonly ILogger _logger = LogManager.GetCurrentClassLogger();

        private IDisposable _gpsNavSwitchDisposable;
        private IDisposable _speedControllerDisposable;
        private IDisposable _imuHeadingUpdateDisposable;

        private readonly AsyncLock _asyncLock = new AsyncLock();

        public WaypointList Waypoints { get; set; } = new WaypointList();

        public bool SpeedControlEnabled { get; set; } = true;

        public GpsNavParameters GpsNavParameters { get; set; } = new GpsNavParameters();

        public int CurrentWaypointIndex { get; set; }

        protected GpsNavigation(CancellationTokenSource cancellationTokenSource, string brokerHostnameOrIp)
            : base(cancellationTokenSource, brokerHostnameOrIp)
        {
            cancellationTokenSource.Token.Register(async () => { await WaypointFollowEnable(false); });
        }

        private bool _followingWaypoints;
        protected bool GetFollowingWaypoints()
        {
            return Volatile.Read(ref _followingWaypoints);
        }

        protected void SetFollowingWaypoints(bool isFollowing)
        {
            Volatile.Write(ref _followingWaypoints, isFollowing);
        }

        private IDisposable _syncImuDisposable;

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

            _syncImuDisposable = Observable.Interval(TimeSpan.FromSeconds(3)).ObserveOnDispatcher().Subscribe(async _ =>
            {


                await SyncImuYaw();
            });

            _gpsDisposable = Gps.GetObservable().ObserveOnDispatcher().Subscribe(async gpsData =>
            {
                if (!GetFollowingWaypoints())
                    return;

                await SetMoveTargets(gpsData);
            });


            _imuHeadingUpdateDisposable = Imu
                .GetReadObservable()
                .ObserveOnDispatcher()
                .Subscribe(async imuData =>
                {
                    try
                    {
                        GpsNavParameters.SetCurrentHeading(imuData.Yaw);

                        if (GetFollowingWaypoints())
                        {
                            //var gpsData = await Gps.GetLatest();
                            //gpsData.Heading = imuData.Yaw;

                            await SetVehicleHeading(
                                GpsNavParameters.GetSteeringDirection(),
                                GpsNavParameters.GetSteeringMagnitude());
                        }
                    }
                    catch (Exception e)
                    {
                        _logger.Log(LogLevel.Error, $"ImuHeadingUpdate failed {e.Message}");

                        await EmergencyBrake();
                    }
                });
        }

        private IDisposable _gpsDisposable;

        private volatile float _startDistanceToWp;

        public async Task WaypointFollowEnable(bool enabled)
        {
            if (GetFollowingWaypoints() == enabled)
                return;

            SetFollowingWaypoints(enabled);

            if (enabled)
            {
                await Lcd.WriteAsync("Started Nav to", 1);
                await Lcd.WriteAsync($"{Waypoints.Count} WPs", 2);

                if (SpeedControlEnabled)
                {
                    _speedControllerDisposable = Observable
                   .Interval(TimeSpan.FromMilliseconds(50))
                   .ObserveOnDispatcher()
                   .Subscribe(async _ =>
                        {
                            await UpdateMoveMagnitude();
                        });
                }

                return;
            }

            await Lcd.WriteAsync("WP Follow finished");
            _logger.Log(LogLevel.Info, "WP Follow finished");

            CurrentWaypointIndex = 0;

            GpsNavParameters.SetLastMoveMagnitude(0);
            GpsNavParameters.SetTargetPpi(0);

            await EmergencyBrake();

            //Cleanup WP Nav specific resources 
            _speedControllerDisposable?.Dispose();
            _speedControllerDisposable = null;
            //-----------------------------------

            
        }

        private async Task UpdateMoveMagnitude()
        {
            var ppi = GpsNavParameters.GetTargetPpi();
            var moveMagnitude = GpsNavParameters.GetLastMoveMagnitude();

            if (!GetFollowingWaypoints())
            {
                GpsNavParameters.SetTargetPpi(0);
                GpsNavParameters.SetLastMoveMagnitude(0);

                await EmergencyBrake();
                return;
            }

            var odometer = await Odometer.GetLatest();

            var pulseCount = 0;

            for (var i = 0; i <= 3; i++)
            {
                pulseCount = pulseCount + odometer.PulseCount;

                odometer = await Odometer.GetLatest();
            }

            pulseCount = pulseCount / 4;

            //Give it some wiggle room
            if (pulseCount < ppi + 30 && pulseCount > ppi - 50)
            {
                return;
            }

            if (pulseCount < ppi)
                moveMagnitude = moveMagnitude + 10;

            if (pulseCount > ppi)
                moveMagnitude = moveMagnitude - .7;

            if (moveMagnitude > 60) //Perhaps .. use pitch accounted for?
                moveMagnitude = 60;

            if (moveMagnitude < 0)
                moveMagnitude = 0;

            await SetVehicleTorque(MovementDirection.Forward, moveMagnitude);

            GpsNavParameters.SetLastMoveMagnitude(moveMagnitude);
        }

        private async Task<bool> CheckWaypointFollowFinished()
        {
            try
            {
                if (CurrentWaypointIndex <= Waypoints.Count && GetFollowingWaypoints())
                    return false;

                await EmergencyBrake();

                _logger.Log(LogLevel.Info, $"Nav finished {Waypoints.Count} WPs");

                await WaypointFollowEnable(false);

                return true;
            }
            catch (Exception e)
            {
                _logger.Log(LogLevel.Info, e.Message);
                return true;
            }
        }

        public async Task SyncImuYaw()
        {
            try
            {
                var imuData = await Imu.GetLatest();
                var diff = imuData.UncorrectedYaw - (await Gps.GetLatest()).Heading;

                Imu.YawCorrection = diff;
            }
            catch (Exception e)
            {
                _logger.Log(LogLevel.Error, $"Sync failed {e.Message}");
            }
        }

        //GPS Heading seems almost useless. Using Yaw instead. OK... Set GPS to "Pedestrian nav mode" heading now seems decent...
        public async Task SetMoveTargets(GpsFixData gpsFixData)
        {
            if (await CheckWaypointFollowFinished())
                return;

            var currentWp = Waypoints[CurrentWaypointIndex];

            try
            {
                var distanceAndHeading = GpsExtensions.GetDistanceAndHeadingToWaypoint(gpsFixData.Lat, gpsFixData.Lon, currentWp.GpsFixData.Lat, currentWp.GpsFixData.Lon);

                GpsNavParameters.SetTargetHeading(distanceAndHeading.HeadingToWaypoint);
                GpsNavParameters.SetDistanceToWaypoint(distanceAndHeading.DistanceInInches);

                GpsNavParameters.SetCurrentHeading(gpsFixData.Heading);

                if (distanceAndHeading.DistanceInInches < Waypoints[CurrentWaypointIndex].Radius)
                {
                    switch (Waypoints[CurrentWaypointIndex].Behaviour)
                    {
                        case WaypointType.Continue:
                            // Stop if I am at the end of my list
                            if (CurrentWaypointIndex + 1 == Waypoints.Count)
                            {
                                await WaypointFollowEnable(false);
                                await CheckWaypointFollowFinished();
                                return;
                            }
                            CurrentWaypointIndex++;
                            return;

                        case WaypointType.Stop:
                            await WaypointFollowEnable(false);
                            await CheckWaypointFollowFinished();
                            break;

                        case WaypointType.Pause:
                            await WaypointFollowEnable(false);
                            CurrentWaypointIndex++;
                            await Task.Delay(1000);
                            await WaypointFollowEnable(true);
                            break;

                        default:
                            break;
                    }
                }
                else
                {
                    _logger.Log(LogLevel.Trace, $"Current Heading: {gpsFixData.Heading}, Heading to WP: {distanceAndHeading.HeadingToWaypoint}");
                    _logger.Log(LogLevel.Trace, $"GPS Distance to WP: {distanceAndHeading.DistanceInInches}in, {distanceAndHeading.DistanceInFeet}ft");

                    GpsNavParameters.SetTargetPpi(510);//This is a pretty even pace, the GPS can keep up with it ok
                    GpsNavParameters.SetLastMoveMagnitude(50);

                    await SetVehicleTorque(MovementDirection.Forward, 50); //GetLatest going quickly, let the speed controller do its work
                }
            }
            catch (Exception e)
            {
                _logger.Log(LogLevel.Info, e.Message);

                await EmergencyBrake();
            }
        }

        private async Task SetVehicleHeading(SteeringDirection direction, double magnitude)
        {
            var steerValue = CenterPwm * 4;

            if (magnitude > 80)
                magnitude = 80;

            switch (direction)
            {
                case SteeringDirection.Left:
                    steerValue = Convert.ToUInt16(magnitude.Map(0, 80, CenterPwm, LeftPwmMax)) * 4;
                    break;
                case SteeringDirection.Right:
                    steerValue = Convert.ToUInt16(magnitude.Map(0, 80, CenterPwm, RightPwmMax)) * 4;
                    break;
            }

            await PwmController.SetChannelValue(steerValue, SteeringChannel);
        }

        private async Task OffsetAllWaypoints(double latOffset, double lonOffset)
        {
            foreach (Waypoint waypoint in Waypoints)
            {
                waypoint.GpsFixData.Lat = waypoint.GpsFixData.Lat + latOffset;
                waypoint.GpsFixData.Lon = waypoint.GpsFixData.Lon + lonOffset;
            }
        }

        private async Task SetVehicleTorque(MovementDirection direction, double magnitude)
        {
            if (Stopped)
            {
                await EmergencyBrake();
                return;
            }

            var moveValue = StoppedPwm * 4;

            switch (direction)
            {
                case MovementDirection.Forward:
                    moveValue = Convert.ToUInt16(magnitude.Map(0, 80, StoppedPwm, ForwardPwmMax)) * 4;
                    break;
                case MovementDirection.Reverse:
                    moveValue = Convert.ToUInt16(magnitude.Map(0, 80, StoppedPwm, ReversePwmMax)) * 4;
                    break;
            }

            await PwmController.SetChannelValue(moveValue, MovementChannel);
        }
    }
}