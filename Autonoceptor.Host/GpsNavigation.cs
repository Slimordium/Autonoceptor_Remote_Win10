using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autonoceptor.Service.Hardware;
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

        private DisplayGroup _displayGroup;
        private DisplayGroup _displayGroupImu;

        public bool SpeedControlEnabled { get; set; } = true;

        private CancellationTokenSource _cruiseControlCancellationTokenSource;

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
                        await WriteToImuLcd($"Yaw: {imuData.Yaw}", $"UYaw: {imuData.UncorrectedYaw}");
                    });

            _gpsLcdLoggerDisposable = Gps
                .GetObservable()
                .Sample(TimeSpan.FromMilliseconds(500))
                .ObserveOnDispatcher()
                .Subscribe(
                    async gpsFixData =>
                    {
                        await WriteToLcd($"{gpsFixData.Lat},{gpsFixData.SatellitesInView},{gpsFixData.Hdop}", $"{gpsFixData.Lon},{gpsFixData.Quality}");
                    });

            var displayGroup = new DisplayGroup
            {
                DisplayItems = new Dictionary<int, string> { { 1, "Init GPS nav" }, { 2, "Complete" } },
                GroupName = "GPSNav"
            };

            var displayGroupImu = new DisplayGroup
            {
                DisplayItems = new Dictionary<int, string> { { 1, "IMU" }, { 2, "" } },
                GroupName = "Imu"
            };

            _displayGroup = await Lcd.AddDisplayGroup(displayGroup);
            _displayGroupImu = await Lcd.AddDisplayGroup(displayGroupImu);
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
        }

        private async Task WriteToLcd(string line1, string line2, bool refreshDisplay = false)
        {
            if (_displayGroup == null)
                return;

            _displayGroup.DisplayItems = new Dictionary<int, string>
            {
                {1, line1 },
                {2, line2 }
            };

            await Lcd.UpdateDisplayGroup(_displayGroup, refreshDisplay);
        }

        private async Task WriteToImuLcd(string line1, string line2, bool refreshDisplay = false)
        {
            if (_displayGroupImu == null)
                return;

            _displayGroupImu.DisplayItems = new Dictionary<int, string>
            {
                {1, line1 },
                {2, line2 }
            };

            await Lcd.UpdateDisplayGroup(_displayGroupImu, refreshDisplay);
        }

        public async Task WaypointFollowEnable(bool enabled)
        {
            if (FollowingWaypoints == enabled)
                return;

            FollowingWaypoints = enabled;

            if (enabled)
            {
                if (_cruiseControlCancellationTokenSource != null && !_cruiseControlCancellationTokenSource.IsCancellationRequested)
                {
                    _cruiseControlCancellationTokenSource.Cancel();
                    _cruiseControlCancellationTokenSource = null;
                }

                _cruiseControlCancellationTokenSource = new CancellationTokenSource();

                await Waypoints.Load(); //Load waypoint file from disk

                if (!Waypoints.Any())
                {
                    FollowingWaypoints = false;

                    await WriteToLcd("No waypoints...", "...found!", true);
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
                            await WaypointFollowEnable(false);
                            return;
                        }

                        //await Odometer.ZeroTripMeter();//0 the trip meter - dont think this is needed? We will see.

                        await SetVehicleHeading(mr.SteeringDirection, mr.SteeringMagnitude);

                        await WriteToLcd($"Hdg {mr.HeadingToTargetWp}", $"D: {mr.DistanceToTargetWp}ft");
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

                if (moveRequest == null)
                {
                    _logger.Log(LogLevel.Info, "Either no waypoints, or there was only one and we were already at it.");
                    await WriteToLcd($"Already at wp?", "", true);

                    return;
                }

                await SetVehicleHeading(moveRequest.SteeringDirection, moveRequest.SteeringMagnitude);

                await WriteToLcd("Started Nav to", $"{Waypoints.Count} WPs", true);

                if (SpeedControlEnabled)
                {
                    await SetCruiseControl(480, _cruiseControlCancellationTokenSource.Token); //UGH, Justin ... I really want to increase this maybe just a smidge? :)
                }

                return;
            }

            _cruiseControlCancellationTokenSource?.Cancel();

            await WriteToLcd("Nav finished to", $"{Waypoints.Count} WPs", true);

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