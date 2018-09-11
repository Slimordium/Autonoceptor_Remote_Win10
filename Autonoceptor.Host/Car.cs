using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.Core;
using Windows.Foundation;
using Windows.UI.Core;
using Autonoceptor.Service.Hardware;
using Autonoceptor.Shared;
using Autonoceptor.Shared.Utilities;
using Newtonsoft.Json;
using Nito.AsyncEx;
using NLog;

namespace Autonoceptor.Host
{
    public class Car : Chassis
    {
        private readonly ILogger _logger = LogManager.GetCurrentClassLogger();

        private volatile int _ppUpdateInterval;

        protected const ushort _extraInputChannel = 14;

        private const ushort _lidarServoChannel = 17;

        private const int _rightLidarPwm = 1056;
        private const int _rightMidLidarPwm = 1282;
        private const int _centerLidarPwm = 1488;
        private const int _leftMidLidarPwm = 1682;
        private const int _leftLidarPwm = 1880;

        private const int _nerfDartChannel = 15;

        private int _safeDistance = 130;

        private List<IDisposable> _sensorDisposables = new List<IDisposable>();

        private IDisposable _odoLcdDisposable;

        private IAsyncAction _lidarTask;

        private DisplayGroup _displayGroup;

        public WaypointQueue Waypoints { get; set; }

        protected string BrokerHostnameOrIp { get; set; }

        private readonly AsyncManualResetEvent _asyncResetEvent = new AsyncManualResetEvent(false);

        public CancellationTokenSource LidarCancellationTokenSource { get; set; } = new CancellationTokenSource();

        public int SafeDistance
        {
            get => Volatile.Read(ref _safeDistance);
            set => Volatile.Write(ref _safeDistance, value);
        } 

        protected Car(CancellationTokenSource cancellationTokenSource, string brokerHostnameOrIp) 
            : base(cancellationTokenSource)
        {
            BrokerHostnameOrIp = brokerHostnameOrIp;

            cancellationTokenSource.Token.Register(async () =>
            {
                await Stop();
                await SetChannelValue(0, _lidarServoChannel);
                await SetChannelValue(0, MovementChannel);
                await SetChannelValue(0, SteeringChannel);
            });
        }

        protected new void DisposeLcdWriters()
        {
            _odoLcdDisposable?.Dispose();
            _odoLcdDisposable = null;

            base.DisposeLcdWriters();
        }

        protected new async Task ConfigureLcdWriters()
        {
            base.ConfigureLcdWriters();

            _odoLcdDisposable = Odometer
                .GetObservable()
                .Sample(TimeSpan.FromMilliseconds(250))
                .ObserveOnDispatcher()
                .Subscribe(
                    async odoData =>
                    {
                        await WriteToLcd($"FPS: {Math.Round(odoData.FeetPerSecond, 1)},PC: {odoData.PulseCount}", $"Trv: {Math.Round(odoData.InTraveled / 12, 1)}ft");
                    });

            var displayGroup = new DisplayGroup
            {
                DisplayItems = new Dictionary<int, string> { { 1, "Init Car" }, { 2, "Complete" } },
                GroupName = "Car"
            };

            _displayGroup = await Lcd.AddDisplayGroup(displayGroup);
        }

        protected new async Task InitializeAsync()
        {
            await base.InitializeAsync();

            Waypoints = new WaypointQueue(.0000001, Lcd);

            await Waypoints.InitializeAsync();

            await Stop();
            await DisableServos();

            StartLidarTask();
        }

        private Thread _speedThread;

        public void StartLidarTask()
        {
            if (LidarCancellationTokenSource == null || !LidarCancellationTokenSource.IsCancellationRequested)
            {
                LidarCancellationTokenSource?.Cancel();
                LidarCancellationTokenSource?.Dispose();
                LidarCancellationTokenSource = new CancellationTokenSource();
            }

            _speedThread = new Thread(async () =>
            {
                await CoreApplication.MainView.CoreWindow.Dispatcher.RunAsync(CoreDispatcherPriority.Normal,
                    async () =>
                    {
                        var safeDistance = SafeDistance;

                        var ir = await SetChannelValue(_rightMidLidarPwm * 4, _lidarServoChannel);

                        await Task.Delay(250);

                        while (!LidarCancellationTokenSource.IsCancellationRequested)
                        {
                            try
                            {
                                if (Stopped)
                                {
                                    await Task.Delay(500);
                                    continue;
                                }

                                var data = new List<LidarData>();

                                for (var pwm = _rightMidLidarPwm; pwm < _leftMidLidarPwm; pwm += 10)
                                {
                                    var r = await SetChannelValue(pwm * 4, _lidarServoChannel);

                                    var lidarData = await Lidar.GetLatest();

                                    if (!lidarData.IsValid)
                                        continue;

                                    lidarData.Angle = Math.Round(pwm.Map(_rightMidLidarPwm, _leftMidLidarPwm, 35, -35));
                                    data.Add(lidarData);

                                    if (lidarData.Distance < safeDistance)
                                    {
                                        if (lidarData.Angle < 0)
                                        {
                                            var newAngle = lidarData.Angle.Map(-35, 0, 0, -35);

                                            await SetVehicleHeadingLidar(SteeringDirection.Right,
                                                Math.Abs(newAngle * 8));
                                        }
                                        else
                                        {
                                            var newAngle = lidarData.Angle.Map(35, 0, 0, 35);
                                            await SetVehicleHeadingLidar(SteeringDirection.Left,
                                                Math.Abs(newAngle * 8));
                                        }
                                    }
                                }

                                var dangerAngles = data.Where(d => d.Distance < safeDistance).ToList();

                                data = new List<LidarData>();

                                for (var pwm = _leftMidLidarPwm; pwm >= _rightMidLidarPwm; pwm -= 10)
                                {
                                    var r = await SetChannelValue(pwm * 4, _lidarServoChannel);

                                    var lidarData = await Lidar.GetLatest();

                                    if (!lidarData.IsValid)
                                        continue;

                                    lidarData.Angle = Math.Round(pwm.Map(_leftMidLidarPwm, _rightMidLidarPwm, -35, 35));
                                    data.Add(lidarData);

                                    if (lidarData.Distance < safeDistance)
                                    {
                                        if (lidarData.Angle < 0)
                                        {
                                            var newAngle = lidarData.Angle.Map(-35, 0, 0, -35);
                                            await SetVehicleHeadingLidar(SteeringDirection.Right,
                                                Math.Abs(newAngle * 8));
                                        }
                                        else
                                        {
                                            var newAngle = lidarData.Angle.Map(35, 0, 0, 35);
                                            await SetVehicleHeadingLidar(SteeringDirection.Left,
                                                Math.Abs(newAngle * 8));
                                        }
                                    }
                                }

                                dangerAngles.AddRange(data.Where(d => d.Distance < safeDistance).ToList());

                                if (dangerAngles.Any())
                                {
                                    _asyncResetEvent.Set();
                                }
                                else
                                {
                                    _asyncResetEvent.Reset();
                                }
                            }
                            catch (Exception e)
                            {
                                _logger.Log(LogLevel.Error, $"Sweep: {e.Message}");
                            }
                        }
                    });
            }) {IsBackground = true};
            _speedThread.Start();
        }

        private async Task SetSteeringOnDangerAngle(List<LidarData> dangerAngles)
        {
            var avgAngle = dangerAngles.Average(a => a.Angle);

            if (avgAngle < 0)
            {
                await SetVehicleHeadingLidar(SteeringDirection.Right, 90);
            }
            else
            {
                await SetVehicleHeadingLidar(SteeringDirection.Left, 90);
            }

            await Task.Delay(250);
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

        protected void ConfigureSensorPublish()
        {
            if (MqttClient == null)
                return;

            if (_sensorDisposables.Any())
            {
                _sensorDisposables.ForEach(disposable =>
                {
                    disposable.Dispose();
                });
            }

            _sensorDisposables = new List<IDisposable>();

            _sensorDisposables.Add(
                Gps.GetObservable()
                    .ObserveOnDispatcher()
                    .Subscribe(async gpsFixData =>
                    {
                        try
                        {
                            await MqttClient.PublishAsync(JsonConvert.SerializeObject(gpsFixData), "autono-gps");
                        }
                        catch (Exception)
                        {
                            //Yum
                        }
                    }));

            _sensorDisposables.Add(
                Lidar.GetObservable()
                    .ObserveOnDispatcher()
                    .Subscribe(async lidarData =>
                    {
                        try
                        {
                            await MqttClient.PublishAsync(JsonConvert.SerializeObject(lidarData), "autono-lidar");
                        }
                        catch (Exception)
                        {
                            //Yum
                        }
                    }));
        }

        private async Task UpdateMoveMagnitude(CancellationToken token)
        {
            var moveMagnitude = 0d;
            var starting = true;
            var isStuck = false;

            while (!token.IsCancellationRequested)
            {
                var pulseCountPerUpdate = _ppUpdateInterval;

                var odometer = await Odometer.GetLatest();

                var pulseCount = odometer.PulseCount;

                if (pulseCount < 100 && !starting)
                {
                    isStuck = true;
                }

                //Give it some wiggle room
                if (pulseCount < pulseCountPerUpdate + 30 && pulseCount > pulseCountPerUpdate - 50)
                {
                    return;
                }

                if (pulseCount < pulseCountPerUpdate)
                    moveMagnitude = moveMagnitude + 30;

                if (pulseCount > pulseCountPerUpdate)
                {
                    starting = false;

                    if (moveMagnitude > 50)
                    {
                        moveMagnitude = moveMagnitude - 5;
                    }
                    else if (moveMagnitude > 40)
                    {
                        moveMagnitude = moveMagnitude - 2;
                    }
                    else
                    {
                        moveMagnitude = moveMagnitude - .7;
                    }
                }

                if (moveMagnitude > 55)
                    moveMagnitude = 55;

                if (moveMagnitude < 0)
                    moveMagnitude = 0;

                if (Stopped)
                    return;

                if (!isStuck)
                {
                    await SetVehicleTorque(MovementDirection.Forward, moveMagnitude);
                }
                else
                {
                    // shoot a nerf dart
                    //await SetChannelValue(1500 * 4, 15);

                    // turn wheels slightly to the left
                    await SetVehicleHeading(SteeringDirection.Left, 70);

                    // reverse
                    await SetVehicleTorque(MovementDirection.Reverse, 60);
                    await Task.Delay(1800, token);

                    // now continue trying to get to next waypoint
                    await SetVehicleTorque(MovementDirection.Forward, 60);

                    isStuck = false;
                }
            }
        }

        protected async Task SetVehicleTorque(MovementDirection direction, double magnitude)
        {
            var moveValue = StoppedPwm * 4;

            if (magnitude > 100)
                magnitude = 100;

            switch (direction)
            {
                case MovementDirection.Forward:
                    moveValue = Convert.ToUInt16(magnitude.Map(0, 100, StoppedPwm, ForwardPwmMax)) * 4;
                    break;
                case MovementDirection.Reverse:
                    moveValue = Convert.ToUInt16(magnitude.Map(0, 100, StoppedPwm, ReversePwmMax)) * 4;
                    break;
            }

            await SetChannelValue(moveValue, MovementChannel);
        }

        public async Task SetCruiseControl(int pulseCountPerUpdateInterval, CancellationToken token)
        {
            _ppUpdateInterval = pulseCountPerUpdateInterval;

            await SetVehicleTorque(MovementDirection.Forward, 60);
        }

        public void UpdateCruiseControl(int pulseCountPerUpdateInterval)
        {
            _ppUpdateInterval = pulseCountPerUpdateInterval;
        }

        public async Task Stop(bool isCanceled = false)
        {
            if (isCanceled)
            {
                Stopped = false;

                await WriteToLcd("Started", "", true);

                return;
            }

            await SetChannelValue(StoppedPwm * 4, MovementChannel);
            await SetChannelValue(0, SteeringChannel);

            if (Stopped)
                return;

            Stopped = true;

            await WriteToLcd("Stopped", "", true);
        }

        public async Task DisableServos()
        {
            await SetChannelValue(0, MovementChannel);
            await SetChannelValue(0, SteeringChannel);
        }

        private async Task SetVehicleHeadingLidar(SteeringDirection direction, double magnitude)
        {
            try
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
            catch (Exception err)
            {
                _logger.Log(LogLevel.Error, $"Error in SetVehicleHeadingLidar: {err}");
            }
        }

        protected async Task SetVehicleHeading(SteeringDirection direction, double magnitude)
        {
            try
            {
                if (Stopped)
                {
                    await SetChannelValue(StoppedPwm * 4, MovementChannel);
                    await SetChannelValue(0, SteeringChannel);
                    return;
                }

                if (!_asyncResetEvent.IsSet) //Will this work? We shall see
                    return;

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
            catch (Exception err)
            {
                _logger.Log(LogLevel.Error, $"Error in SetVehicleHeading: {err}");
            }
        }
    }
}