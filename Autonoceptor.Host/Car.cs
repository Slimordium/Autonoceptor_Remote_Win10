using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.UI.Xaml.Media;
using Autonoceptor.Service.Hardware;
using Autonoceptor.Shared;
using Autonoceptor.Shared.Utilities;
using Newtonsoft.Json;
using NLog;
using RxMqtt.Shared;

namespace Autonoceptor.Host
{
    public class Car : Chassis
    {
        private readonly ILogger _logger = LogManager.GetCurrentClassLogger();

        protected const ushort _extraInputChannel = 14;

        private const ushort _lidarServoChannel = 17;

        private const int _rightLidarPwm = 1056;
        private const int _rightMidLidarPwm = 1371;
        private const int _centerLidarPwm = 1472;
        private const int _leftMidLidarPwm = 1572;
        private const int _leftLidarPwm = 1880;

        private const int _nerfDartChannel = 15;

        private int _safeDistance = 100;

        public int SafeDistance
        {
            get => Volatile.Read(ref _safeDistance);
            set => Volatile.Write(ref _safeDistance, value);
        } 

        private List<IDisposable> _sensorDisposables = new List<IDisposable>();

        private IDisposable _odoLcdDisposable;

        private Task _lidarTask;
        private Task _speedControlTask;

        private DisplayGroup _displayGroup;

        public WaypointQueue Waypoints { get; set; }

        protected string BrokerHostnameOrIp { get; set; }

        protected Car(CancellationTokenSource cancellationTokenSource, string brokerHostnameOrIp) 
            : base(cancellationTokenSource)
        {
            BrokerHostnameOrIp = brokerHostnameOrIp;

            cancellationTokenSource.Token.Register(async () => { await Stop(); });
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

        public CancellationTokenSource LidarCancellationTokenSource { get; set; } = new CancellationTokenSource();

        protected new async Task InitializeAsync()
        {
            await base.InitializeAsync();

            Waypoints = new WaypointQueue(.0000001, Lcd);

            await Waypoints.InitializeAsync();

            await Stop();
            await DisableServos();

            StartLidarTask();
        }

        public void StartLidarTask()
        {
            if (!LidarCancellationTokenSource.IsCancellationRequested)
            {
                LidarCancellationTokenSource.Cancel();
                LidarCancellationTokenSource = new CancellationTokenSource();
            }

            _lidarTask = new Task(async () =>
            {
                var safeDistance = SafeDistance;

                while (!LidarCancellationTokenSource.IsCancellationRequested)
                {
                    try
                    {
                        var sweepData = await Sweep(Host.Sweep.Center);

                        var dangerAngles = sweepData.Where(d => d.Distance < safeDistance).ToList();

                        if (!dangerAngles.Any())
                        {
                            continue;
                        }

                        var dangerAngle = dangerAngles.Average(d => d.Angle);

                        if (dangerAngle < 0)
                        {
                            await SetVehicleHeading(SteeringDirection.Right, dangerAngle);
                        }
                        else
                        {
                            await SetVehicleHeading(SteeringDirection.Left, dangerAngle);
                        }
                    }
                    catch (Exception e)
                    {
                        _logger.Log(LogLevel.Error, $"Sweep: {e.Message}");
                    }
                }
            });
            _lidarTask.Start();
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

        private async Task UpdateMoveMagnitude(double pulseCountPerUpdate, CancellationToken token)
        {
            var moveMagnitude = 0d;
            var starting = true;
            var isStuck = false;

            while (!token.IsCancellationRequested)
            {
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
            _speedControlTask?.Dispose();
            _speedControlTask = null;

            await SetVehicleTorque(MovementDirection.Forward, 100);

            _speedControlTask = UpdateMoveMagnitude(pulseCountPerUpdateInterval, token);
            _speedControlTask.Start();
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

        private async Task<List<LidarData>> Sweep(Sweep sweep)
        {
            try
            {
                var data = new List<LidarData>();

                switch (sweep)
                {
                    case Host.Sweep.Left:
                        {
                            for (var pwm = _centerLidarPwm; pwm < _leftLidarPwm; pwm += 10)
                            {
                                await SetChannelValue(pwm * 4, _lidarServoChannel);

                                var lidarData = await Lidar.GetLatest();
                                
                                if (!lidarData.IsValid)
                                    continue;

                                lidarData.Angle = Math.Round(pwm.Map(_leftMidLidarPwm, _leftLidarPwm, 0, -45));
                                data.Add(lidarData);
                            }

                            break;
                        }
                    case Host.Sweep.Right:
                        {
                            for (var pwm = _centerLidarPwm; pwm > _rightLidarPwm; pwm -= 10)
                            {
                                await SetChannelValue(pwm * 4, _lidarServoChannel);

                                var lidarData = await Lidar.GetLatest();
                                
                                if (!lidarData.IsValid)
                                    continue;

                                lidarData.Angle = Math.Round(pwm.Map(_rightMidLidarPwm, _rightLidarPwm, 0, 45)); ;
                                data.Add(lidarData);
                            }

                            break;
                        }
                    case Host.Sweep.Center:
                        {
                            // sweep left 15 degrees
                            for (var pwm = _centerLidarPwm; pwm < _leftMidLidarPwm; pwm += 10)
                            {
                                await SetChannelValue(pwm * 4, _lidarServoChannel);

                                var lidarData = await Lidar.GetLatest();

                                if (!lidarData.IsValid)
                                    continue;

                                lidarData.Angle = Math.Round(pwm.Map(_centerLidarPwm, _leftMidLidarPwm, 0, -15));
                                data.Add(lidarData);
                            }

                            await Task.Delay(250);

                            // sweep right 30 degrees
                            for (var pwm = _leftMidLidarPwm; pwm > _rightMidLidarPwm; pwm -= 10)
                            {
                                await SetChannelValue(pwm * 4, _lidarServoChannel);

                                var lidarData = await Lidar.GetLatest();

                                if (!lidarData.IsValid)
                                    continue;

                                lidarData.Angle = Math.Round(pwm.Map(_leftMidLidarPwm, _rightMidLidarPwm, -15, 15));
                                data.Add(lidarData);
                            }

                            break;
                        }
                }

                await Task.Delay(500);
                await SetChannelValue(_centerLidarPwm * 4, _lidarServoChannel);

                return await Task.FromResult(data);
            }
            catch (Exception err)
            {
                _logger.Log(LogLevel.Error, "Error in SweepInternal");
                _logger.Log(LogLevel.Error, err.Message);
                return await Task.FromResult(new List<LidarData>(null));
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
                _logger.Log(LogLevel.Error, "Error in SetVehicleHeading");
                _logger.Log(LogLevel.Error, err);
            }
        }
    }
}