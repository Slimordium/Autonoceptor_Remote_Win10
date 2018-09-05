﻿using System;
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
        private readonly ILogger _logger = LogManager.GetCurrentClassLogger();

        private IDisposable _xboxButtonDisposable;
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
                .Sample(TimeSpan.FromMilliseconds(40))
                .ObserveOnDispatcher()
                .Subscribe(async xboxData =>
                {
                    await OnNextXboxData(xboxData);
                });

            _xboxButtonDisposable = XboxDevice.GetObservable()
                .Where(xboxData => xboxData != null && xboxData.FunctionButtons.Any())
                .Throttle(TimeSpan.FromMilliseconds(250))
                .ObserveOnDispatcher()
                .Subscribe(async xboxData =>
                {
                    await OnNextXboxButtonData(xboxData);
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

                        if (XboxDevice != null && !GetFollowingWaypoints()) //Controller was connected, and not following waypoints, so stop
                        {
                            await Stop();
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
            if (Stopped)
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

            if (GetFollowingWaypoints())
                return;

            await SetChannelValue(steeringPwm, SteeringChannel);
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
            }

            if (xboxData.FunctionButtons.Contains(FunctionButton.B))
            {
                var gpsFix = await Gps.GetLatest();
                var imu = await Imu.GetLatest();

                Waypoints.Add(new Waypoint
                {
                    GpsFixData = gpsFix,
                    ImuData = imu
                });

                await Lcd.WriteAsync($"WP {Waypoints.Count} added");

                _logger.Log(LogLevel.Info, $"WP Lat: {gpsFix.Lat}, Lon: { gpsFix.Lon}, {gpsFix.Quality}");

                await Waypoints.Save();
                return;
            }

            if (xboxData.FunctionButtons.Contains(FunctionButton.Start))
            {
                if (GetFollowingWaypoints())
                    return;

                _logger.Log(LogLevel.Info, $"Starting WP follow {Waypoints.Count} WPs");
                await WaypointFollowEnable(true);
                return;
            }

            if (xboxData.FunctionButtons.Contains(FunctionButton.Y))
            {
                Waypoints = new WaypointList();

                _logger.Log(LogLevel.Info, "WPs Cleared");
                await Lcd.WriteAsync($"WPs cleared");
            }
        }
    }
}