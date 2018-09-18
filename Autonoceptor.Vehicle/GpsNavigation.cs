using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autonoceptor.Hardware.Lcd;
using Autonoceptor.Shared.Gps;
using Autonoceptor.Shared.Imu;
using Autonoceptor.Shared.Utilities;
using Newtonsoft.Json;
using NLog;

namespace Autonoceptor.Vehicle
{
    public class GpsNavigation : Car
    {
        private readonly ILogger _logger = LogManager.GetCurrentClassLogger();

        private IDisposable _syncImuDisposable;
        private IDisposable _imuUpdateDisposable;
        private IDisposable _gpsUpdateDisposable;
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
                .Where(gpsFixData => gpsFixData != null)
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

        private async Task UpdateGpsNav(GpsFixData gpsData)
        {
            try
            {
                var imuData = await Imu.GetLatest();

                //var distanceAndHeadingDebug = GpsExtensions.GetDistanceAndHeadingToWaypoint(gpsData.Lat, gpsData.Lon, Waypoints.CurrentWaypoint.Lat, Waypoints.CurrentWaypoint.Lon);

                var mr = await Waypoints.GetMoveRequestForNextWaypoint(gpsData.Lat, gpsData.Lon, imuData.Yaw);

                //await MqttClient.PublishAsync(JsonConvert.SerializeObject(distanceAndHeadingDebug), "autono-gpsDistanceHeading").ConfigureAwait(false);
                //await MqttClient.PublishAsync(JsonConvert.SerializeObject(mr), "autono-moveRequest").ConfigureAwait(false);

                //await MqttClient.PublishAsync(JsonConvert.SerializeObject(gpsData), "autono-gps").ConfigureAwait(false);

                if (mr == null)
                {
                    if (!FollowingWaypoints)
                        return;

                    await WaypointFollowEnable(false);
                    return;
                }

                if (mr.DistanceInToTargetWp < 60)
                    await UpdateCruiseControl(2);

                await SetVehicleHeading(mr.SteeringDirection, mr.SteeringMagnitude);
            }
            catch (Exception e)
            {
                _logger.Log(LogLevel.Error, $"GpsHeadingUpdate failed {e.Message}");

                await Stop();
            }
        }

        private async Task UpdateImuNav(ImuData imuData)
        {
            try
            {
                var gpsData = await Gps.GetLatest();

                //var distanceAndHeadingDebug = GpsExtensions.GetDistanceAndHeadingToWaypoint(gpsData.Lat, gpsData.Lon, Waypoints.CurrentWaypoint.Lat, Waypoints.CurrentWaypoint.Lon);
                //await MqttClient.PublishAsync(JsonConvert.SerializeObject(distanceAndHeadingDebug), "autono-imuDistanceHeading").ConfigureAwait(false);

                //await MqttClient.PublishAsync(JsonConvert.SerializeObject(imuData), "autono-imu").ConfigureAwait(false);

                var mr = await Waypoints.GetMoveRequestForNextWaypoint(gpsData.Lat, gpsData.Lon, imuData.Yaw);

                if (mr == null)
                {
                    if (!FollowingWaypoints)
                        return;

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
        }

        public async Task WaypointFollowEnable(bool enabled)
        {
            FollowingWaypoints = enabled;

            if (enabled)
            {
                var load = await Waypoints.Load(); //Load waypoint file from disk

                if (!load)
                {
                    FollowingWaypoints = false;

                    await Lcd.Update(GroupName.Waypoint, "Waypoint load...", "...failed", true);
                    return;
                }

                if (!Waypoints.Any())
                {
                    FollowingWaypoints = false;

                    await Lcd.Update(GroupName.Waypoint, "No waypoints...", "...found!", true);
                    return;
                }

                _gpsUpdateDisposable = Gps
                    .GetObservable()
                    .Where(d => d != null)
                    .ObserveOnDispatcher()
                    .Subscribe(async gpsData => { await UpdateGpsNav(gpsData); });

                _imuUpdateDisposable = Imu
                    .GetReadObservable()
                    .Where(d => d != null)
                    .ObserveOnDispatcher()
                    .Subscribe(async imuData => { await UpdateImuNav(imuData); });

                var currentLocation = await Gps.GetLatest();
                var moveRequest = await Waypoints.GetMoveRequestForNextWaypoint(currentLocation.Lat, currentLocation.Lon, currentLocation.Heading);

                if (moveRequest == null)
                {
                    _logger.Log(LogLevel.Info, "Either no waypoints, or there was only one and we were already at it.");
                    await Lcd.Update(GroupName.Waypoint, $"Already at wp?", string.Empty, true);

                    return;
                }

                //StartLidarThread();

                await Stop(true);

                await SetVehicleHeading(moveRequest.SteeringDirection, moveRequest.SteeringMagnitude);

                await Lcd.Update(GroupName.Waypoint, "Started Nav", string.Empty, true);

                if (SpeedControlEnabled)
                {
                    await SetCruiseControlFps(4);
                }

                return;
            }

            await StopCruiseControl();

            //StopLidarThread();

            await Lcd.Update(GroupName.Waypoint, "Nav stopped", string.Empty, true);

            await Stop();

            _gpsUpdateDisposable?.Dispose();
            _imuUpdateDisposable?.Dispose();

            _gpsUpdateDisposable = null;
            _imuUpdateDisposable = null;
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