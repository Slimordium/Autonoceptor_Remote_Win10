using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Autonoceptor.Host
{
    public class Car : Chassis
    {
        protected const int RightPwmMax = 1861;
        protected const int CenterPwm = 1321;
        protected const int LeftPwmMax = 837;

        protected const int ReversePwmMax = 1072;
        protected const int StoppedPwm = 1471;
        protected const int ForwardPwmMax = 1856;

        protected const ushort MovementChannel = 0;
        protected const ushort SteeringChannel = 1;

        protected const ushort GpsNavEnabledChannel = 12;
        protected const ushort _extraInputChannel = 13;
        private const ushort _enableMqttChannel = 14;

        private IDisposable _enableMqttDisposable;

        private List<IDisposable> _sensorDisposables = new List<IDisposable>();

        private bool _stopped;
        protected bool Stopped
        {
            get => Volatile.Read(ref _stopped);
            set => Volatile.Write(ref _stopped, value);
        }

        protected string BrokerHostnameOrIp { get; set; }

        protected Car(CancellationTokenSource cancellationTokenSource, string brokerHostnameOrIp) 
            : base(cancellationTokenSource)
        {
            BrokerHostnameOrIp = brokerHostnameOrIp;

            cancellationTokenSource.Token.Register(async () => { await EmergencyBrake(); });

        }

        protected new async Task InitializeAsync()
        {
            await base.InitializeAsync();

            _enableMqttDisposable = PwmController.GetObservable()
                .Where(channel => channel.ChannelId == _enableMqttChannel)
                .ObserveOnDispatcher()
                .Subscribe(
                    async channel =>
                    {
                        if (channel.DigitalValue)
                        {
                            await InitializeMqtt(BrokerHostnameOrIp);

                            return;
                        }

                        MqttClient?.Dispose();
                        MqttClient = null;
                    });
        }

        public async Task EmergencyBrake(bool isCanceled = false)
        {
            if (isCanceled)
            {
                if (Stopped)
                    return;

                Stopped = false;

                await Lcd.WriteAsync("E-Brake canceled");

                return;
            }

            if (Stopped)
                return;

            Stopped = true;

            await Stop();
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

        private async Task Stop()
        {
            await PwmController.SetChannelValue(StoppedPwm - 40 * 4, MovementChannel); //Momentary reverse ... helps stop quickly

            await Task.Delay(30);

            await PwmController.SetChannelValue(StoppedPwm * 4, MovementChannel);

            await DisableServos();
        }

        public async Task DisableServos()
        {
            await PwmController.SetChannelValue(0, MovementChannel);
            await PwmController.SetChannelValue(0, SteeringChannel);

            await Task.Delay(100);
        }
    }
}