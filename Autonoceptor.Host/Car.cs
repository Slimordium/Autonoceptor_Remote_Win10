using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
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

        private int _safeDistance = 50;

        public int SafeDistance
        {
            get => Volatile.Read(ref _safeDistance);
            set => Volatile.Write(ref _safeDistance, value);
        } 

        private List<IDisposable> _sensorDisposables = new List<IDisposable>();
        private IDisposable _speedControllerDisposable;

        public WaypointQueue Waypoints { get; set; }

        protected string BrokerHostnameOrIp { get; set; }

        protected Car(CancellationTokenSource cancellationTokenSource, string brokerHostnameOrIp) 
            : base(cancellationTokenSource)
        {
            BrokerHostnameOrIp = brokerHostnameOrIp;

            cancellationTokenSource.Token.Register(async () => { await Stop(); });
        }

        private DisplayGroup _displayGroup;

        protected new void DisposeLcdWriters()
        {
            _odoLcdDisposable?.Dispose();
            _odoLcdDisposable = null;

            base.DisposeLcdWriters();
        }

        protected new async Task ConfigureLcdWriters()
        {
            base.ConfigureLcdWriters();

            _odoLcdDisposable = Odometer.GetObservable().Sample(TimeSpan.FromMilliseconds(250)).ObserveOnDispatcher()
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

        private double _pulseCountPerUpdate;
        private double _moveMagnitude;
        private IDisposable _odoLcdDisposable;

        private bool _gettingUnstuck = false;

        private async Task UpdateMoveMagnitude()
        {
            var moveMagnitude = Volatile.Read(ref _moveMagnitude);
            var pulseCountPerUpdate = Volatile.Read(ref _pulseCountPerUpdate);

            if (pulseCountPerUpdate < 1) //Probably stopped...
                return;

            var odometer = await Odometer.GetLatest();

            var pulseCount = odometer.PulseCount;

            bool isStuck = pulseCount == 0;

            //Give it some wiggle room
            if (pulseCount < pulseCountPerUpdate + 30 && pulseCount > pulseCountPerUpdate - 50)
            {
                return;
            }

            if (pulseCount < pulseCountPerUpdate)
                moveMagnitude = moveMagnitude + 40;

            if (pulseCount > pulseCountPerUpdate)
            {
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

            if (!isStuck && !_gettingUnstuck)
                await SetVehicleTorque(MovementDirection.Forward, moveMagnitude);
            else
            {
                _gettingUnstuck = true;

                // shoot a nerf dart
                //await SetChannelValue(1500 * 4, 15);

                // turn wheels slightly to the left
                var turnMagnitude = 15;
                await SetChannelValue(Convert.ToUInt16(turnMagnitude.Map(0, 100, CenterPwm, LeftPwmMax)) * 4, SteeringChannel);

                // reverse with wheels turned for 1 second
                await SetVehicleTorque(MovementDirection.Reverse, 35);
                await Task.Delay(1500);

                // now continue trying to get to next waypoint
                await SetVehicleTorque(MovementDirection.Forward, moveMagnitude);

                _gettingUnstuck = false;
            }

            Volatile.Write(ref _moveMagnitude, moveMagnitude);
        }

   

        protected async Task SetVehicleTorque(MovementDirection direction, double magnitude)
        {
            var moveValue = StoppedPwm * 4;

            if (magnitude > 80)
                magnitude = 80;

            switch (direction)
            {
                case MovementDirection.Forward:
                    moveValue = Convert.ToUInt16(magnitude.Map(0, 80, StoppedPwm, ForwardPwmMax)) * 4;
                    break;
                case MovementDirection.Reverse:
                    moveValue = Convert.ToUInt16(magnitude.Map(0, 80, StoppedPwm, ReversePwmMax)) * 4;
                    break;
            }

            await SetChannelValue(moveValue, MovementChannel);
        }

        public void EnableCruiseControl(int pulseCountPerUpdateInterval)
        {
            _speedControllerDisposable?.Dispose();
            _speedControllerDisposable = null;

            Volatile.Write(ref _moveMagnitude, 30);
            Volatile.Write(ref _pulseCountPerUpdate, pulseCountPerUpdateInterval);

            _speedControllerDisposable = Observable
                .Interval(TimeSpan.FromMilliseconds(100))
                .ObserveOnDispatcher()
                .Subscribe(async _ =>
                {
                    Thread.Sleep(1);
                    if (Volatile.Read(ref _gettingUnstuck))
                    {
                        Thread.Sleep(1);
                        return;
                    }

                    await UpdateMoveMagnitude();
                });
        }

        public void DisableCruiseControl()
        {
            Volatile.Write(ref _moveMagnitude, 0);
            Volatile.Write(ref _pulseCountPerUpdate, 0);

            _speedControllerDisposable?.Dispose();
            _speedControllerDisposable = null;
        }

        public void SetCruiseControl(int pulseCountPerUpdateInterval)
        {
            Volatile.Write(ref _moveMagnitude, 0);
            Volatile.Write(ref _pulseCountPerUpdate, pulseCountPerUpdateInterval);
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
                                Thread.Sleep(1);
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
                                Thread.Sleep(1);
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
                Thread.Sleep(1);
                await Task.Delay(500);
                Thread.Sleep(1);
                await SetChannelValue(_centerLidarPwm * 4, _lidarServoChannel);
                Thread.Sleep(1);
                await Task.Delay(500);
                Thread.Sleep(1);
                return data;
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

                if (Volatile.Read(ref _gettingUnstuck)) //We are letting the "GetUnstuck" method handle ... getting ... unstuck!
                {
                    Thread.Sleep(1);
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