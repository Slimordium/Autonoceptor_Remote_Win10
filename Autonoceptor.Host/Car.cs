using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autonoceptor.Service.Hardware;
using Autonoceptor.Shared.Utilities;
using Newtonsoft.Json;
using NLog;
using RxMqtt.Shared;

namespace Autonoceptor.Host
{
    public class Car : Chassis
    {
        private readonly ILogger _logger = LogManager.GetCurrentClassLogger();


        protected const ushort _extraInputChannel = 13;
        private const ushort _enableMqttChannel = 14;

        private IDisposable _enableMqttDisposable;

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

        protected new async Task InitializeAsync()
        {
            await base.InitializeAsync();

            Waypoints = new WaypointQueue(.0000001, Lcd);

            var displayGroup = new DisplayGroup
            {
                DisplayItems = new Dictionary<int, string> { { 1, "Init Car" }, { 2, "Complete" } },
                GroupName = "Car"
            };

            await Lcd.AddDisplayGroup(displayGroup);

            _enableMqttDisposable = PwmObservable
                .Where(channel => channel.ChannelId == _enableMqttChannel)
                .ObserveOnDispatcher()
                .Subscribe(
                    async channel =>
                    {
                        if (channel.DigitalValue)
                        {
                            var status = await InitializeMqtt(BrokerHostnameOrIp);

                            if (status == Status.Initialized)
                                ConfigureSensorPublish();

                            return;
                        }

                        MqttClient?.Dispose();
                        MqttClient = null;
                    });

            await Stop();
            await DisableServos();
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

        private async Task UpdateMoveMagnitude()
        {
            var moveMagnitude = Volatile.Read(ref _moveMagnitude);
            var pulseCountPerUpdate = Volatile.Read(ref _pulseCountPerUpdate);

            if (pulseCountPerUpdate < 1) //Probably stopped...
                return; 

            var odometer = await Odometer.GetLatest();

            var pulseCount = odometer.PulseCount;

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

            await SetVehicleTorque(MovementDirection.Forward, moveMagnitude);

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

                await Lcd.WriteAsync("Started");

                _logger.Log(LogLevel.Info, "Started");

                return;
            }

            await SetChannelValue(StoppedPwm * 4, MovementChannel);
            await SetChannelValue(0, SteeringChannel);

            if (Stopped)
                return;

            Stopped = true;

            await Lcd.WriteAsync("Stopped");
            _logger.Log(LogLevel.Info, "Stopped");
        }

        public async Task DisableServos()
        {
            await SetChannelValue(0, MovementChannel);
            await SetChannelValue(0, SteeringChannel);

            await Task.Delay(100);

            _logger.Log(LogLevel.Info, "PWM Off");
        }
    }
}