using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Devices.HumanInterfaceDevice;
using Autonoceptor.Service.Hardware;
using Autonoceptor.Shared.Utilities;
using Hardware.Xbox;
using Hardware.Xbox.Enums;
using NLog;

namespace Autonoceptor.Host
{
    public class XboxController : GpsNavigation
    {
        private readonly ILogger _logger = LogManager.GetCurrentClassLogger();

        private IDisposable _xboxButtonDisposable;
        private IDisposable _xboxDisposable;
        private IDisposable _xboxConnectedCheckDisposable;
        private IDisposable _xboxDpadDisposable;

        public XboxController(CancellationTokenSource cancellationTokenSource, string brokerHostnameOrIp) 
            : base(cancellationTokenSource, brokerHostnameOrIp)
        {
        }
         
        private async Task ConfigureXboxObservable()
        {
            _xboxDisposable?.Dispose();
            _xboxButtonDisposable?.Dispose();
            _xboxDpadDisposable?.Dispose();

            _xboxDisposable = null;
            _xboxButtonDisposable = null;
            _xboxDpadDisposable = null;

            if (XboxDevice == null)
                return;

            _xboxDisposable = XboxDevice.GetObservable()
                .Where(xboxData => xboxData != null)
                .Sample(TimeSpan.FromMilliseconds(40))
                .ObserveOnDispatcher()
                .Subscribe(async xboxData =>
                {
                    await OnNextXboxData(xboxData);
                });

            _xboxButtonDisposable = XboxDevice.GetObservable()
                .Where(xboxData => xboxData != null && xboxData.FunctionButtons.Any())
                .Throttle(TimeSpan.FromMilliseconds(125))
                .ObserveOnDispatcher()
                .Subscribe(async xboxData =>
                {
                    await OnNextXboxButtonData(xboxData);
                });

            _xboxDpadDisposable = XboxDevice.GetObservable()
                .Where(xboxData => xboxData != null && xboxData.Dpad != Direction.None)
                .Throttle(TimeSpan.FromMilliseconds(125))
                .ObserveOnDispatcher()
                .Subscribe(async xboxData =>
                {
                    await OnNextXboxDpadData(xboxData);
                });

            await Lcd.WriteAsync("Initialized Xbox");
        }

        public new async Task InitializeAsync()
        {
            await base.InitializeAsync();

            var displayGroup = new DisplayGroup
            {
                DisplayItems = new Dictionary<int, string> { { 1, "Init XBox" }, { 2, "Complete" } },
                GroupName = "Xbox"
            };

            await Lcd.AddDisplayGroup(displayGroup);

            _xboxConnectedCheckDisposable = Observable
                .Interval(TimeSpan.FromMilliseconds(1000))
                .ObserveOnDispatcher()
                .Subscribe(
                    async _ =>
                    {
                        var devices = await DeviceInformation.FindAllAsync(HidDevice.GetDeviceSelector(0x01, 0x05));

                        if (devices.Any() && XboxDevice == null)
                        {
                            try
                            {
                                var init = await InitializeXboxController();

                                if (!init)
                                    return;

                                _logger.Log(LogLevel.Error, $"Xbox connected");

                                await ConfigureXboxObservable();
                            }
                            catch (Exception e)
                            {
                                _logger.Log(LogLevel.Error, $"Xbox re-init: {e.Message}");
                            }
                           
                            return;
                        }

                        if (devices.Any() || XboxDevice == null)
                            return;

                        await DisposeXboxResources();

                        if (!FollowingWaypoints)
                            await Stop();
                    });

            await ConfigureXboxObservable();
        }

        private async Task DisposeXboxResources()
        {
            _xboxDisposable?.Dispose();
            _xboxButtonDisposable?.Dispose();
            _xboxDpadDisposable?.Dispose();

            _xboxDisposable = null;
            _xboxButtonDisposable = null;
            _xboxDpadDisposable = null;

            await Task.Delay(250);

            XboxDevice?.Dispose();

            XboxDevice = null;
        }

        private async Task OnNextXboxData(XboxData xboxData)
        {
            if (Stopped || FollowingWaypoints)
                return;

            ushort steeringPwm = CenterPwm * 4;

            switch (xboxData.RightStick.Direction)
            {
                case Direction.UpLeft:
                case Direction.DownLeft:
                case Direction.Left:
                    steeringPwm = Convert.ToUInt16(xboxData.RightStick.Magnitude.Map(0, 10000, CenterPwm, LeftPwmMax) * 4);
                    break;
                case Direction.UpRight:
                case Direction.DownRight:
                case Direction.Right:
                    steeringPwm = Convert.ToUInt16(xboxData.RightStick.Magnitude.Map(0, 10000, CenterPwm, RightPwmMax) * 4);
                    break;
            }

            var reverseMagnitude = Convert.ToUInt16(xboxData.LeftTrigger.Map(0, 33000, StoppedPwm, ReversePwmMax) * 4);
            var forwardMagnitude = Convert.ToUInt16(xboxData.RightTrigger.Map(0, 33000, StoppedPwm, ForwardPwmMax) * 4);

            var movePwm = forwardMagnitude;

            if (reverseMagnitude < 5500)
            {
                movePwm = reverseMagnitude;
            }

            await SetChannelValue(movePwm, MovementChannel);

            await SetChannelValue(steeringPwm, SteeringChannel);
        }

        private async Task OnNextXboxDpadData(XboxData xboxData)
        {
            switch (xboxData.Dpad)
            {
                case Direction.Right:
                case Direction.DownRight:
                case Direction.UpRight:
                    await Lcd.NextGroup();
                    break;
                case Direction.Left:
                case Direction.DownLeft:
                case Direction.UpLeft:
                    await Lcd.PreviousGroup();
                    break;
            }
        }

        private async Task OnNextXboxButtonData(XboxData xboxData)
        {
            if (xboxData.FunctionButtons.Contains(FunctionButton.X))
            {
                await Stop();

                await WaypointFollowEnable(false);

                return;
            }

            if (xboxData.FunctionButtons.Contains(FunctionButton.A))
            {
                await Stop(true);
                return;
            }

            if (FollowingWaypoints)
                return;

            if (xboxData.FunctionButtons.Contains(FunctionButton.B))
            {
                var gpsFix = await Gps.GetLatest();

                await Waypoints.Enqueue(new Waypoint
                {
                    Lat = gpsFix.Lat,
                    Lon = gpsFix.Lon,
                });

                await Lcd.WriteAsync($"WP {Waypoints.Count} added");

                _logger.Log(LogLevel.Info, $"WP Lat: {gpsFix.Lat}, Lon: { gpsFix.Lon}, {gpsFix.Quality}");

                await Waypoints.Save();
                return;
            }

            if (xboxData.FunctionButtons.Contains(FunctionButton.Start))
            {
                _logger.Log(LogLevel.Info, $"Starting WP follow {Waypoints.Count} WPs");
                await WaypointFollowEnable(true);
                return;
            }

            if (xboxData.FunctionButtons.Contains(FunctionButton.Y))
            {
                Waypoints.Clear();

                Waypoints.CurrentWaypoint = null;

                await Waypoints.Save();

                _logger.Log(LogLevel.Info, "WPs Cleared");
                await Lcd.WriteAsync($"WPs cleared");
            }

            if (xboxData.FunctionButtons.Contains(FunctionButton.BumperLeft))
            {
                await Waypoints.AddStartingPoint(await Gps.GetLatest);
            }

            if (xboxData.FunctionButtons.Contains(FunctionButton.BumperRight))
            {
                await Waypoints.IterateStartingPoint();
            }

            if (xboxData.FunctionButtons.Contains(FunctionButton.Back))
            {
                await Waypoints.SetStartingPoint(await Gps.GetLatest);
            }

        }
    }
}