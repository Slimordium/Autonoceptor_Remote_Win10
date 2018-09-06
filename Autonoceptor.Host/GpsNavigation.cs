using System;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autonoceptor.Shared.Utilities;
using Nito.AsyncEx;
using NLog;

namespace Autonoceptor.Host
{
    public class GpsNavigation : LidarNavOverride
    {
        private readonly ILogger _logger = LogManager.GetCurrentClassLogger();

        private IDisposable _syncImuDisposable;
        private IDisposable _gpsNavSwitchDisposable;
        private IDisposable _imuHeadingUpdateDisposable;
        private IDisposable _gpsDisposable;



        public bool SpeedControlEnabled { get; set; } = true;

        protected GpsNavigation(CancellationTokenSource cancellationTokenSource, string brokerHostnameOrIp)
            : base(cancellationTokenSource, brokerHostnameOrIp)
        {
            cancellationTokenSource.Token.Register(async () =>
            {
                await Stop();
                await WaypointFollowEnable(false); 

            });
            
            //Somewhere in my driveway...
            Waypoints.AddFirst(new Waypoint{Lat = 40.147721, Lon = -105.110756 });//40.147721, -105.110756
        }

        private bool _followingWaypoints;
    
        protected bool FollowingWaypoints
        {
            get => Volatile.Read(ref _followingWaypoints);
            set => Volatile.Write(ref _followingWaypoints, value);
        }

        protected new async Task InitializeAsync()
        {
            await base.InitializeAsync();

            _gpsNavSwitchDisposable = PwmObservable
                .Where(channel => channel.ChannelId == GpsNavEnabledChannel)
                .ObserveOnDispatcher()
                .Subscribe(async channelData =>
                {
                    await WaypointFollowEnable(channelData.DigitalValue);
                });

            _syncImuDisposable = Observable
                .Interval(TimeSpan.FromSeconds(4))
                .ObserveOnDispatcher()
                .Subscribe(async _ =>
                {
                    await SyncImuYaw();
                });
        }

        public async Task WaypointFollowEnable(bool enabled)
        {
            if (FollowingWaypoints == enabled)
                return;

            FollowingWaypoints = enabled;

            if (enabled && Waypoints.Count > 0)
            {
                await Waypoints.ResetActiveWaypoints();

                _gpsDisposable = Gps
                    .GetObservable()
                    .Where(d => d != null)
                    .ObserveOnDispatcher()
                    .Subscribe(async gpsData =>
                    {
                        var mr = await Waypoints.GetMoveRequestForNextWaypoint(gpsData.Lat, gpsData.Lon, gpsData.Heading);

                        if (mr == null)
                        {
                            await WaypointFollowEnable(false);
                            return;
                        }

                        await SetVehicleHeading(mr.SteeringDirection, mr.SteeringMagnitude);
                    });

                _imuHeadingUpdateDisposable = Imu
                    .GetReadObservable()
                    .Where(d => d != null)
                    .ObserveOnDispatcher()
                    .Subscribe(async imuData =>
                    {
                        try
                        {
                            var gpsData = await Gps.GetLatest();

                            var mr = await Waypoints.GetMoveRequestForNextWaypoint(gpsData.Lat, gpsData.Lon, imuData.Yaw);

                            if (mr == null)
                            {
                                await WaypointFollowEnable(false);
                                return;
                            }

                            await SetVehicleHeading(mr.SteeringDirection, mr.SteeringMagnitude);
                        }
                        catch (Exception e)
                        {
                            _logger.Log(LogLevel.Error, $"ImuHeadingUpdate failed {e.Message}");

                            await Stop();
                        }
                    });

                var currentLocation = await Gps.GetLatest();
                var moveRequest = await Waypoints.GetMoveRequestForNextWaypoint(currentLocation.Lat, currentLocation.Lon, currentLocation.Heading);

                await SetVehicleHeading(moveRequest.SteeringDirection, moveRequest.SteeringMagnitude);

                await Lcd.WriteAsync("Started Nav to", 1);
                await Lcd.WriteAsync($"{Waypoints.Count} WPs", 2);

                if (SpeedControlEnabled)
                {
                    EnableCruiseControl(320);
                }

                return;
            }

            DisableCruiseControl();

            await Lcd.WriteAsync("WP Follow finished");
            _logger.Log(LogLevel.Info, "WP Follow finished");

            await Stop();

            _gpsDisposable?.Dispose();
            _imuHeadingUpdateDisposable?.Dispose();

            _gpsDisposable = null;
            _imuHeadingUpdateDisposable = null;
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

        private async Task SetVehicleHeading(SteeringDirection direction, double magnitude)
        {
            var steerValue = CenterPwm * 4;

            if (magnitude > 100)
                magnitude = 100;

            switch (direction)
            {
                case SteeringDirection.Left:
                    steerValue = Convert.ToUInt16(magnitude.Map(0, 100, CenterPwm, LeftPwmMax)) * 4;
                    break;
                case SteeringDirection.Right:
                    steerValue = Convert.ToUInt16(magnitude.Map(0, 100, CenterPwm, RightPwmMax)) * 4;
                    break;
            }

            await SetChannelValue(steerValue, SteeringChannel);
        }
    }
}