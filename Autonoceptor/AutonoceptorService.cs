using System;
using System.Collections.Generic;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using Autonoceptor.Service.Hardware;
using Hardware.Xbox;
using Newtonsoft.Json;
using NLog;
using RxMqtt.Client;
using RxMqtt.Shared;

namespace Autonoceptor.Service
{
    public class AutonoceptorService
    {
        private List<IDisposable> _disposables = new List<IDisposable>();

        private readonly List<Task> _startTasks = new List<Task>();
        private readonly ILogger _logger = LogManager.GetCurrentClassLogger();

        private readonly MaxbotixSonar _maxbotixSonar = new MaxbotixSonar();
        
        private readonly Gps _gps = new Gps();

        private MqttClient _mqttClient;

        private readonly string _brokerIp;

        private CancellationToken _cancellationToken;

        private readonly AutonoceptorController _autonoceptorController = new AutonoceptorController();
        public AutonoceptorService(string brokerIp)
        {
            _brokerIp = brokerIp;
        }

        public async Task StartAsync(CancellationToken cancellationToken, MqttClient mqttClient)
        {
            _mqttClient = mqttClient;

            //await InitMqtt();

            _cancellationToken = cancellationToken;
            _cancellationToken.Register(() =>
            {
                DisposeSubscriptions();
                _mqttClient?.Dispose();
            });

            await _maxbotixSonar.InitializeAsync();
            await _autonoceptorController.InitializeAsync();
            //await _gps.InitializeAsync();
            
            //_startTasks.Add(_maxbotixSonar.StartAsync(_cancellationToken));
            //_startTasks.Add(_autonoceptorController.StartAsync(_cancellationToken));
            //_startTasks.Add(_gps.StartAsync(_cancellationToken));

            Subscribe();

            await Task.WhenAll(_startTasks.ToArray());
        }

        private async Task<Status> InitMqtt()
        {
            var status = Status.Error;

            try
            {
                _mqttClient = new MqttClient("autonoceptor", _brokerIp, 1883);

                status = await _mqttClient.InitializeAsync();

                _logger.Log(LogLevel.Info, $"MQTT Client Started => {status}");
            }
            catch (Exception e)
            {
                _logger.Log(LogLevel.Error, e.Message);
            }

            if (status != Status.Initialized)
                DisposeSubscriptions();

            return status;
        }

        //public void Subscribe()
        //{
        //    _disposables.Add(_mqttClient.GetPublishStringObservable("autono-xbox")
        //        .SubscribeOn(Scheduler.Default)
        //        .Subscribe(async s =>
        //        {
        //            if (s == null)
        //                return;

        //            try
        //            {
        //                await _autonoceptorController.XboxData(JsonConvert.DeserializeObject<XboxData>(s));
        //            }
        //            catch (Exception)
        //            {
        //                //
        //            }
        //        }));

        //    _disposables.Add(_gps.GpsFixSubject
        //        .Where(fix => fix != null)
        //        .SubscribeOn(Scheduler.Default)
        //        .Subscribe(async fix =>
        //        {
        //            try
        //            {
        //                await _mqttClient.PublishAsync(JsonConvert.SerializeObject(fix), "autono-gps", TimeSpan.FromSeconds(2));
        //            }
        //            catch (TimeoutException)
        //            {
        //                //
        //            }
        //        }));

        //    _disposables.Add(_maxbotixSonar.SonarSubject
        //        .SubscribeOn(Scheduler.Default)
        //        .Subscribe(async sonar =>
        //        {
        //            try
        //            {
        //                await _mqttClient.PublishAsync(sonar.ToString(), "autono-sonar", TimeSpan.FromSeconds(2));
        //            }
        //            catch (TimeoutException)
        //            {
        //                //
        //            }
        //        }));
        //}

        public void DisposeSubscriptions()
        {
            foreach (var disposable in _disposables)
            {
                disposable.Dispose();
            }

            _disposables = new List<IDisposable>();
        }
    }
}