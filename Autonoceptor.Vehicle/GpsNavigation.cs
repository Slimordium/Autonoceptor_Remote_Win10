using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autonoceptor.Hardware.Lcd;
using NLog;

namespace Autonoceptor.Host
{
    public class GpsNavigation : Car
    {
        private readonly ILogger _logger = LogManager.GetCurrentClassLogger();

        private IDisposable _syncImuDisposable;
        private IDisposable _imuHeadingUpdateDisposable;
        private IDisposable _gpsDisposable;
        private IDisposable _imuLcdLoggerDisposable;
        private IDisposable _gpsLcdLoggerDisposable;

        public bool SpeedControlEnabled { get; set; } = true;

        protected GpsNavigation(CancellationTokenSource cancellationTokenSource, string brokerHostnameOrIp)
            : base(cancellationTokenSource, brokerHostnameOrIp)
        {
            cancellationTokenSource.Token.Register(async () =>
            {
                await Stop();
                await WaypointFollowEnable(false); 

            });
        }

        private bool _followingWaypoints;

        protected bool FollowingWaypoints
        {
            get => Volatile.Read(ref _followingWaypoints);
            set => Volatile.Write(ref _followingWaypoints, value);
        }

        protected new void DisposeLcdWriters()
        {
            _imuLcdLoggerDisposable?.Dispose();
            _gpsLcdLoggerDisposable?.Dispose();

            _imuLcdLoggerDisposable = null;
            _gpsLcdLoggerDisposable = null;

            base.DisposeLcdWriters();
        }

        protected new async Task ConfigureLcdWriters()
        {
            await base.ConfigureLcdWriters();

            _imuLcdLoggerDisposable = Imu
                .GetReadObservable()
                .Sample(TimeSpan.FromMilliseconds(250))
                .ObserveOnDispatcher()
                .Subscribe(
                    async imuData =>
                    {
                        await Lcd.Update(GroupName.Imu, $"Yaw: {imuData.Yaw}", $"UYaw: {imuData.UncorrectedYaw}");
                    });

            _gpsLcdLoggerDisposable = Gps
                .GetObservable()
                .ObserveOnDispatcher()
                .Subscribe(
                    async gpsFixData =>
                    {
                        await Lcd.Update(GroupName.Gps1,
                            $"{gpsFixData.Lat}",
                            $"{gpsFixData.Lon}");

                        await Lcd.Update(GroupName.Gps2,
                            $"S: {gpsFixData.SatellitesInView} HDOP: {gpsFixData.Hdop}",
                            $"Q: {gpsFixData.Quality}");
                    });
        }

        protected new async Task InitializeAsync()
        {
            await base.InitializeAsync();

            _syncImuDisposable = Observable
                .Interval(TimeSpan.FromSeconds(4)) //sync every 8-12ft when moving
                .ObserveOnDispatcher()
                .Subscribe(async _ =>
                {
                    await SyncImuYaw();
                });

            await ConfigureLcdWriters();
        }

        public async Task WaypointFollowEnable(bool enabled)
        {
            FollowingWaypoints = enabled;

            if (enabled)
            {
                await Waypoints.Load(); //Load waypoint file from disk

                if (!Waypoints.Any())
                {
                    FollowingWaypoints = false;

                    await Lcd.Update(GroupName.Waypoint, "No waypoints...", "...found!", true);
                    return;
                }

                _gpsDisposable = Gps
                    .GetObservable()
                    .Where(d => d != null)
                    .ObserveOnDispatcher()
                    .Subscribe(async gpsData =>
                    {
                        var mr = await Waypoints.GetMoveRequestForNextWaypoint(gpsData.Lat, gpsData.Lon, gpsData.Heading);

                        if (mr == null)
                        {
                            if (!FollowingWaypoints)
                                return;

                            await WaypointFollowEnable(false);
                            return;
                        }

                        if (mr.Distance < 60)
                            UpdateCruiseControl(310);

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
                                if (!FollowingWaypoints)
                                    return;

                                await WaypointFollowEnable(false);
                                return;
                            }

                            await SetVehicleHeading(mr.SteeringDirection, mr.SteeringMagnitude);

                            await Lcd.Update(GroupName.GpsNavDistHeading, $"Heading: {mr.HeadingToTargetWp}", $"Distance: {mr.DistanceToTargetWp}ft");
                        }
                        catch (Exception e)
                        {
                            _logger.Log(LogLevel.Error, $"ImuHeadingUpdate failed {e.Message}");

                            await Stop();
                        }
                    });

                var currentLocation = await Gps.GetLatest();
                var moveRequest = await Waypoints.GetMoveRequestForNextWaypoint(currentLocation.Lat, currentLocation.Lon, currentLocation.Heading);

                if (moveRequest == null)
                {
                    _logger.Log(LogLevel.Info, "Either no waypoints, or there was only one and we were already at it.");
                    await Lcd.Update(GroupName.Waypoint, $"Already at wp?", "", true);

                    return;
                }

                await SetVehicleTorque(MovementDirection.Forward, 30);

                await SetVehicleHeading(moveRequest.SteeringDirection, moveRequest.SteeringMagnitude);

                await Lcd.Update(GroupName.Waypoint, "Started Nav", string.Empty, true);

                if (SpeedControlEnabled)
                {
                    await SetCruiseControl(330);
                }

                return;
            }

            await StopCruiseControl();

            await Lcd.Update(GroupName.Waypoint, "Nav finished to", $"{Waypoints.Count} WPs", true);

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
    }
}