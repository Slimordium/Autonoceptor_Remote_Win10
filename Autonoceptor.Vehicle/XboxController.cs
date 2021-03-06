﻿using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Devices.HumanInterfaceDevice;
using Autonoceptor.Hardware.Lcd;
using Autonoceptor.Shared.Gps;
using Autonoceptor.Shared.Utilities;
using Hardware.Xbox;
using Hardware.Xbox.Enums;
using Newtonsoft.Json;
using NLog;

namespace Autonoceptor.Vehicle
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
                .Sample(TimeSpan.FromMilliseconds(250))
                .ObserveOnDispatcher()
                .Subscribe(async xboxData =>
                {
                    await OnNextXboxButtonData(xboxData);
                });

            _xboxDpadDisposable = XboxDevice.GetObservable()
                .Where(xboxData => xboxData != null && xboxData.Dpad != Direction.None)
                .Sample(TimeSpan.FromMilliseconds(250))
                .ObserveOnDispatcher()
                .Subscribe(async xboxData =>
                {
                    await OnNextXboxDpadData(xboxData);
                });
        }

        public new async Task InitializeAsync()
        {
            await base.InitializeAsync();

            //_gpsNavSwitchDisposable = PwmObservable
            //    .Where(channel => channel.ChannelId == GpsNavEnabledChannel)
            //    .ObserveOnDispatcher()
            //    .Subscribe(async channelData =>
            //    {
            //        DisposeLcdWriters();

            //        await Task.Delay(250);

            //        await WaypointFollowEnable(channelData.DigitalValue);
            //    });

            //_enableLcdDisposable = PwmObservable
            //    .Where(channel => channel.ChannelId == _enableLidarChannel)
            //    .ObserveOnDispatcher()
            //    .Subscribe(
            //        channel =>
            //        {
            //            if (!channel.DigitalValue)
            //            {
            //                LidarCancellationTokenSource?.Cancel(false);
            //                LidarCancellationTokenSource?.Dispose();
            //                return;
            //            }

            //            StartLidarThread();
            //        });

            _xboxConnectedCheckDisposable = Observable
                .Interval(TimeSpan.FromSeconds(1))
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
            if (FollowingWaypoints)
                return;

            var steeringDirection = SteeringDirection.Center;

            switch (xboxData.RightStick.Direction)
            {
                case Direction.UpLeft:
                case Direction.DownLeft:
                case Direction.Left:
                    steeringDirection = SteeringDirection.Left;
                    break;
                case Direction.UpRight:
                case Direction.DownRight:
                case Direction.Right:
                    steeringDirection = SteeringDirection.Right;
                    break;
            }

            var steeringMagnitude = Math.Round(xboxData.RightStick.Magnitude.Map(0, 10000, 0, 100));

            var reverseMagnitude = Math.Round(xboxData.LeftTrigger.Map(0, 33000, 0, 100));
            var forwardMagnitude = Math.Round(xboxData.RightTrigger.Map(0, 33000, 0, 100));

            var movementDirection = MovementDirection.Forward;
            var movementMagnitude = forwardMagnitude;

            if (reverseMagnitude > forwardMagnitude)
            {
                movementDirection = MovementDirection.Reverse;
                movementMagnitude = reverseMagnitude;
            }

            await SetVehicleHeadingAndTorque(steeringDirection, steeringMagnitude, movementDirection, movementMagnitude);
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

                    await Lcd.InvokeUpCallback();
                    break;
                case Direction.Down:

                    await Lcd.InvokeDownCallback();
                    break;
            }
        }

        private async Task OnNextXboxButtonData(XboxData xboxData)
        {
            if (xboxData.FunctionButtons.Contains(FunctionButton.Back))
            {
                await Waypoints.Save();

                await Lcd.Update(GroupName.Waypoint, $"{Waypoints.Count} WPs...", "...saved", true);

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
                    await Lcd.Update(GroupName.Waypoint, "Stopped WP...", "...following", true);
                    await WaypointFollowEnable(false);
                }
                else
                {
                    await Lcd.Update(GroupName.Waypoint, "Started WP...", "...following", true);
                    await WaypointFollowEnable(true);
                }

                return;
            }

            if (FollowingWaypoints)
                return;

            if (xboxData.FunctionButtons.Contains(FunctionButton.B))
            {
                var gpsFix = await Gps.GetLatest();

                var wp = new Waypoint
                {
                    Lat = gpsFix.Lat,
                    Lon = gpsFix.Lon,
                };

                await Waypoints.Enqueue(wp);

                //await MqttClient.PublishAsync(JsonConvert.SerializeObject(wp), "autono-waypoint").ConfigureAwait(false);

                _logger.Log(LogLevel.Info, $"WP Lat: {gpsFix.Lat}, Lon: { gpsFix.Lon}, {gpsFix.Quality}");

                await Lcd.Update(GroupName.WaypointInfo, $"WP: {gpsFix.Lat}", $"{ gpsFix.Lon}");

                await Lcd.Update(GroupName.Waypoint, $"WP {Waypoints.Count}...", "...queued", true);

                return;
            }

            if (xboxData.FunctionButtons.Contains(FunctionButton.Y))
            {
                Waypoints.Clear();

                Waypoints.CurrentWaypoint = null;

                await Waypoints.Save();

                await Lcd.Update(GroupName.Waypoint, $"WPs Cleared", "...", true);
            }
        }
    }
}