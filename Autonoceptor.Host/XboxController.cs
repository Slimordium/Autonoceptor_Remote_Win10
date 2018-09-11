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
        private IDisposable _gpsNavSwitchDisposable;

        private const ushort _enableLcdChannel = 13;
        private const ushort _enableLidarChannel = 14;
        private IDisposable _enableLcdDisposable;

        private DisplayGroup _displayGroup;

        public XboxController(CancellationTokenSource cancellationTokenSource, string brokerHostnameOrIp) 
            : base(cancellationTokenSource, brokerHostnameOrIp)
        {
        }
         
        private void ConfigureXboxObservable()
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
                .Sample(TimeSpan.FromMilliseconds(125))
                .ObserveOnDispatcher()
                .Subscribe(async xboxData =>
                {
                    await OnNextXboxButtonData(xboxData);
                });

            _xboxDpadDisposable = XboxDevice.GetObservable()
                .Where(xboxData => xboxData != null && xboxData.Dpad != Direction.None)
                .Sample(TimeSpan.FromMilliseconds(125))
                .ObserveOnDispatcher()
                .Subscribe(async xboxData =>
                {
                    await OnNextXboxDpadData(xboxData);
                });
        }

        protected new async Task ConfigureLcdWriters()
        {
            await base.ConfigureLcdWriters();

            var displayGroup = new DisplayGroup
            {
                DisplayItems = new Dictionary<int, string> { { 1, "Init XBox" }, { 2, "Complete" } },
                GroupName = "Xbox"
            };

            _displayGroup = await Lcd.AddDisplayGroup(displayGroup);
        }

        private async Task WriteToLcd(string line1, string line2, bool display = false)
        {
            if (_displayGroup == null)
                return;

            _displayGroup.DisplayItems = new Dictionary<int, string>
            {
                {1, line1 },
                {2, line2 }
            };

            await Lcd.UpdateDisplayGroup(_displayGroup, display);
        }

        public new async Task InitializeAsync()
        {
            await base.InitializeAsync();

            _gpsNavSwitchDisposable = PwmObservable
                .Where(channel => channel.ChannelId == GpsNavEnabledChannel)
                .ObserveOnDispatcher()
                .Subscribe(async channelData =>
                {
                    DisposeLcdWriters();

                    await Task.Delay(500);

                    await WaypointFollowEnable(channelData.DigitalValue);
                });

            _enableLcdDisposable = PwmObservable
                .Where(channel => channel.ChannelId == _enableLcdChannel || 
                                  channel.ChannelId == _enableLidarChannel)
                .Sample(TimeSpan.FromMilliseconds(500))
                .ObserveOnDispatcher()
                .Subscribe(
                    async channel =>
                    {
                        if (channel.ChannelId == _enableLidarChannel)
                        {
                            if (!channel.DigitalValue)
                            {
                                LidarCancellationTokenSource?.Cancel(false);
                                LidarCancellationTokenSource?.Dispose();
                                return;
                            }

                            StartLidarTask();

                            return;
                        }

                        if (channel.ChannelId == _enableLcdChannel)
                        {
                            if (channel.DigitalValue)
                            {
                                await ConfigureLcdWriters();

                                await Lcd.WriteAsync("LCD Writers", 1);
                                await Lcd.WriteAsync("Configured", 2);

                                _logger.Log(LogLevel.Info, "LCD Writers Configured");

                                return;
                            }

                            await Lcd.WriteAsync("LCD Writers", 1);
                            await Lcd.WriteAsync("Disposed", 2);

                            _logger.Log(LogLevel.Info, "LCD Writers Disposed");

                            DisposeLcdWriters();
                            return;
                        }
                    });

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

                                ConfigureXboxObservable();
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

            ConfigureXboxObservable();
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
                    await Lcd.NextGroup();
                    break;
                case Direction.Left:
                    await Lcd.PreviousGroup();
                    break;
                case Direction.Up:

                    //Because it is volatile 
                    var sdu = SafeDistance;
                    sdu = sdu + 2;

                    if (sdu > 200)
                        sdu = 200;

                    SafeDistance = sdu;

                    await WriteToLcd($"Safe distance", $"    {sdu}", true);

                    break;
                case Direction.Down:

                    //Because it is volatile 
                    var sdd = SafeDistance;
                    sdd = sdd - 2;

                    if (sdd < 20)
                        sdd = 20;

                    SafeDistance = sdd;

                    await WriteToLcd($"Safe distance", $"    {sdd}", true);

                    break;
            }
        }

        private async Task OnNextXboxButtonData(XboxData xboxData)
        {
            if (xboxData.FunctionButtons.Contains(FunctionButton.Back))
            {
                await Waypoints.Save();

                await WriteToLcd($"{Waypoints.Count} WPs...", "...saved", true);

                return;
            }

            if (xboxData.FunctionButtons.Contains(FunctionButton.X))
            {
                await Stop();

                return;
            }

            if (xboxData.FunctionButtons.Contains(FunctionButton.A))
            {
                await Stop(true);

                return;
            }

            if (xboxData.FunctionButtons.Contains(FunctionButton.Start))
            {
                if (FollowingWaypoints)
                {
                    _logger.Log(LogLevel.Info, $"Stopping WP follow {Waypoints.Count} WPs");

                    await WaypointFollowEnable(false);
                }
                else
                {
                    _logger.Log(LogLevel.Info, $"Starting WP follow {Waypoints.Count} WPs");
                    await WaypointFollowEnable(true);
                }

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

                _logger.Log(LogLevel.Info, $"WP Lat: {gpsFix.Lat}, Lon: { gpsFix.Lon}, {gpsFix.Quality}");

                await WriteToLcd($"WP {Waypoints.Count}...", "...queued", true);

                return;
            }

            if (xboxData.FunctionButtons.Contains(FunctionButton.Y))
            {
                Waypoints.Clear();

                Waypoints.CurrentWaypoint = null;

                await Waypoints.Save();

                _logger.Log(LogLevel.Info, "WPs Cleared");
            }
        }
    }
}