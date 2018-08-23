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

namespace Autonoceptor.Host
{
    public class XboxController : GpsNavigation
    {
        private IDisposable _xboxDisposable;
        private readonly IDisposable _xboxConnectedCheckDisposable;

        public XboxController(CancellationTokenSource cancellationTokenSource, string brokerHostnameOrIp) 
            : base(cancellationTokenSource, brokerHostnameOrIp)
        {
            _xboxConnectedCheckDisposable = Observable.Interval(TimeSpan.FromMilliseconds(250))
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

            await ConfigureXboxObservable();
        }

        private async Task OnNextXboxData(XboxData xboxData)
        {
            if (xboxData.FunctionButtons.Contains(FunctionButton.A))
            {
                await EmergencyBrake(true);
            }

            if (xboxData.FunctionButtons.Contains(FunctionButton.B))
            {
                Waypoints.Add(CurrentLocation);
                await Lcd.WriteAsync($"WP {Waypoints.Count} added");

                await Waypoints.Save();
                return;
            }

            if (xboxData.FunctionButtons.Contains(FunctionButton.Start))
            {
                await WaypointFollowEnable(true);
                return;
            }

            if (xboxData.FunctionButtons.Contains(FunctionButton.X))
            {
                await WaypointFollowEnable(false);
                await EmergencyBrake();
                return;
            }

            if (xboxData.FunctionButtons.Contains(FunctionButton.Y))
            {
                Waypoints = new WaypointList();

                await Lcd.WriteAsync($"WPs cleared");
                return;
            }

            if (Stopped)
                return;

            //TODO: Fix D-Pad functionality in xbox, use it to increase/decrease speed while navigating waypoints
            if (FollowingWaypoints)
                return;

            var direction = CenterPwm * 4;

            switch (xboxData.RightStick.Direction)
            {
                case Direction.UpLeft:
                case Direction.DownLeft:
                case Direction.Left:
                    direction = Convert.ToUInt16(xboxData.RightStick.Magnitude.Map(0, 10000, CenterPwm, LeftPwmMax)) * 4;
                    break;
                case Direction.UpRight:
                case Direction.DownRight:
                case Direction.Right:
                    direction = Convert.ToUInt16(xboxData.RightStick.Magnitude.Map(0, 10000, CenterPwm, RightPwmMax)) * 4;
                    break;
            }

            await PwmController.SetChannelValue(direction, SteeringChannel); //ChannelId 1 is Steering

            var reverseMagnitude = Convert.ToUInt16(xboxData.LeftTrigger.Map(0, 33000, StoppedPwm, ReversePwmMax)) * 4;
            var forwardMagnitude = Convert.ToUInt16(xboxData.RightTrigger.Map(0, 33000, StoppedPwm, ForwardPwmMax)) * 4;

            var outputVal = forwardMagnitude;

            if (reverseMagnitude < 5500 || Stopped)
            {
                outputVal = reverseMagnitude;
            }

            await PwmController.SetChannelValue(outputVal, MovementChannel); //ChannelId 0 is the motor driver
        }
    }
}