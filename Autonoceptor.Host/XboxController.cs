using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Devices.HumanInterfaceDevice;
using Autonoceptor.Shared.Utilities;
using Hardware.Xbox;
using Hardware.Xbox.Enums;
using NLog;

namespace Autonoceptor.Host
{
    public class XboxController : GpsNavigation
    {
        private IDisposable _xboxDisposable;
        private IDisposable _xboxConnectedCheckDisposable;

        public XboxController(CancellationTokenSource cancellationTokenSource, string brokerHostnameOrIp) 
            : base(cancellationTokenSource, brokerHostnameOrIp)
        {
        }

        private async Task ConfigureXboxObservable()
        {
            _xboxDisposable?.Dispose();

            if (XboxDevice == null)
                return;

            _xboxDisposable = XboxDevice.GetObservable()
                .Where(xboxData => xboxData != null)
                .ObserveOnDispatcher()
                .Subscribe(async xboxData =>
                {
                    await OnNextXboxData(xboxData);
                });

            await Lcd.WriteAsync("Initialized Xbox");
        }

        public new async Task InitializeAsync()
        {
            await base.InitializeAsync();

            _xboxConnectedCheckDisposable = Observable
                .Interval(TimeSpan.FromMilliseconds(500))
                .ObserveOnDispatcher()
                .Subscribe(
                    async _ =>
                    {
                        var devices = await DeviceInformation.FindAllAsync(HidDevice.GetDeviceSelector(0x01, 0x05));

                        if (devices.Any())
                            return;

                        if (XboxDevice != null && !FollowingWaypoints) //Controller was connected, and not following waypoints, so stop
                        {
                            await EmergencyBrake();
                        }
                        else
                        {
                            var init = await InitializeXboxController();

                            if (!init)
                                return;

                            await ConfigureXboxObservable();
                        }
                    });

            await ConfigureXboxObservable();
        }

        private async Task OnNextXboxData(XboxData xboxData)
        {
            if (xboxData.FunctionButtons.Contains(FunctionButton.A))
            {
                await EmergencyBrake(true);
                return;
            }

            if (xboxData.FunctionButtons.Contains(FunctionButton.B))
            {
                var wp = Gps.CurrentLocation;

                Waypoints.Add(wp);
                await Lcd.WriteAsync($"WP {Waypoints.Count} added");

                Logger.Log(LogLevel.Info, $"WP @ Lat: {wp.Lat}, Lon: { wp.Lon}");

                await Waypoints.Save();
                return;
            }

            if (xboxData.FunctionButtons.Contains(FunctionButton.Start))
            {
                Logger.Log(LogLevel.Info, $"Starting WP follow {Waypoints.Count} WPs");
                await WaypointFollowEnable(true);
                return;
            }

            if (xboxData.FunctionButtons.Contains(FunctionButton.X))
            {
                Logger.Log(LogLevel.Info, "Stopping...");
                await WaypointFollowEnable(false);
                await EmergencyBrake();
                return;
            }

            if (xboxData.FunctionButtons.Contains(FunctionButton.Y))
            {
                Waypoints = new WaypointList();

                Logger.Log(LogLevel.Info, "WPs Cleared");
                await Lcd.WriteAsync($"WPs cleared");
                return;
            }

            //TODO: Fix D-Pad functionality in xbox, use it to increase/decrease speed while navigating waypoints
            if (FollowingWaypoints)
                return;

            ushort direction = CenterPwm * 4;

            switch (xboxData.RightStick.Direction)
            {
                case Direction.UpLeft:
                case Direction.DownLeft:
                case Direction.Left:
                    direction = Convert.ToUInt16(xboxData.RightStick.Magnitude.Map(0, 10000, CenterPwm, LeftPwmMax) * 4);
                    break;
                case Direction.UpRight:
                case Direction.DownRight:
                case Direction.Right:
                    direction = Convert.ToUInt16(xboxData.RightStick.Magnitude.Map(0, 10000, CenterPwm, RightPwmMax) * 4);
                    break;
            }

            var reverseMagnitude = Convert.ToUInt16(xboxData.LeftTrigger.Map(0, 33000, StoppedPwm, ReversePwmMax) * 4);
            var forwardMagnitude = Convert.ToUInt16(xboxData.RightTrigger.Map(0, 33000, StoppedPwm, ForwardPwmMax) * 4);

            var outputVal = forwardMagnitude;

            if (reverseMagnitude < 5500)
            {
                outputVal = reverseMagnitude;
            }

            await WriteToHardware(direction, outputVal); //ChannelId 1 is Steering
        }
    }
}