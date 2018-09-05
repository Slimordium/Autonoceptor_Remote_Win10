using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
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