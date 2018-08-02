using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.ExtendedExecution;
using Windows.Media.SpeechSynthesis;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Imaging;
using Caliburn.Micro;
using Hardware.Xbox;
using Autonoceptor.Remote.Views;
using Autonoceptor.Shared.OpenCv;
using Newtonsoft.Json;
using NLog;
using RxMqtt.Client;
using RxMqtt.Shared;

namespace Autonoceptor.Remote.ViewModels
{
    public class ShellViewModel : Screen
    {
        private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();

        private List<IDisposable> _disposables = new List<IDisposable>();

        private XboxDevice _xboxController;

        public IObservableCollection<string> Log { get; set; } = new BindableCollection<string>();

        private readonly ILogger _logger = NLog.LogManager.GetCurrentClassLogger();

        public MediaElement MediaElement { get; } = new MediaElement();

        public string TextForSpeech { get; set; } = "Test";

        private MqttClient _mqttClient;
        public string BrokerIp { get; set; } = "127.0.0.1";
        public string SubTopic { get; set; }
        public string PubTopic { get; set; }
        public string PubMessage { get; set; }

        public int ImageWidth { get; set; } = 1000;
    
        public int GaitSpeed { get; set; } = 30;
        public int BodyHeight { get; set; } = 60;
        public int LegLiftHeight { get; set; } = 60;
        public int UpdateInterval { get; set; } = 500;

        public bool StreamChanges { get; set; }

        public WriteableBitmap HexImage { get; set; }

        public bool CirclesEnabled { get; set; } = false;

        public bool LinesEnabled { get; set; } = false;

        public ShellViewModel()
        {
            Application.Current.Suspending += Current_Suspending;
            Application.Current.Resuming += CurrentOnResuming;
        }

        private async void CurrentOnResuming(object sender, object o)
        {
            await Connect();

            await Task.Delay(1000);

            await StartCamera();

            await SetUpdateInterval();
        }

        private void Current_Suspending(object sender, Windows.ApplicationModel.SuspendingEventArgs e)
        {
            _mqttClient?.Dispose();
        }

        public async Task SetUpdateInterval()
        {
            if (!StreamChanges)
            {
                foreach (var disposable in _disposables) disposable.Dispose();

                _disposables = new List<IDisposable>();

                UpdateSubscriptions();

                return;
            }

            _xboxController = new XboxDevice();

            await _xboxController.InitializeAsync(_cancellationTokenSource.Token);

            UpdateSubscriptions();
        }

        public async Task StartCar()
        {
            await _mqttClient.PublishAsync("", "autono-car-start");

            _heartbeat?.Dispose();
            _heartbeat = null;

            _heartbeat = Observable.Interval(TimeSpan.FromSeconds(1)).ObserveOn(Scheduler.Default).Subscribe(async _ =>
            {
                await _mqttClient.PublishAsync("", "autono-heartbeat");
            });
        }

        public async Task StartCamera()
        {
            await _mqttClient.PublishAsync("6", "autono-camera-start");
        }

        public int SelectedResolution { get; set; } = 6;

        public async Task SetResolution()
        {
             await _mqttClient.PublishAsync(SelectedResolution.ToString(), "autono-camera-start");
        }

        private IDisposable _heartbeat;

        public float HoughDp { get; set; } = 1;
        public float HoughMinDistance { get; set; } = 10;
        public int HoughCannyThreshold { get; set; } = 150;
        public int HoughVotesThreshold { get; set; } = 60;
        public int HoughMinRadius { get; set; } = 2;
        public int HoughMaxRadius { get; set; } = 140;
        public int HoughMaxCircles { get; set; } = 8;

        public double CannyHigh { get; set; } = 5;

        public double CannyLow { get; set; } = 50;

        private ExtendedExecutionSession _session;

        private async Task RequestExtendedSession()
        {
            _session = new ExtendedExecutionSession { Reason = ExtendedExecutionReason.LocationTracking };
            _session.Revoked += NewSessionOnRevoked;
            var sessionResult = await _session.RequestExtensionAsync();

            switch (sessionResult)
            {
                case ExtendedExecutionResult.Allowed:
                    AddToLog("Session extended");
                    break;

                case ExtendedExecutionResult.Denied:
                    AddToLog("Session extend denied");
                    break;
            }
        }

        private IDisposable _sessionExtendTimer;

        private async Task Connect()
        {
            if (_sessionExtendTimer != null)
            {
                _session.Dispose();
                _session = null;

                _sessionExtendTimer.Dispose();
            }

            _sessionExtendTimer = Observable.Interval(TimeSpan.FromMinutes(5)).Subscribe(async _ => { await RequestExtendedSession(); });

            await RequestExtendedSession();

            //_amazonRekognitionClient = new AmazonRekognitionClient("", "", new AmazonRekognitionConfig
            //{
            //    RegionEndpoint = Amazon.RegionEndpoint.USEast1,
            //}); 

            _mqttClient = new MqttClient($"AutonoceptorRemote-{DateTime.Now.Minute}-{DateTime.Now.Millisecond}", BrokerIp, 1883);

            var result = await _mqttClient.InitializeAsync();

            AddToLog($"MQTT Connection => '{result}'");

            if (result != Status.Initialized)
                return;

            UpdateSubscriptions();
        }

        private void NewSessionOnRevoked(object sender, ExtendedExecutionRevokedEventArgs args)
        {
            _session?.Dispose();
            _session = null;
        }

        private void UpdateSubscriptions()
        {
            try
            {
                foreach (var disposable in _disposables)
                    disposable.Dispose();
            }
            catch (Exception)
            {
                //
            }

            _disposables = new List<IDisposable>();

            if (_xboxController != null && _xboxController.IsConnected)
            {
                _disposables.Add(_xboxController.GetObservable()
                    .Sample(TimeSpan.FromMilliseconds(Convert.ToInt64(UpdateInterval)))
                    .SubscribeOn(Scheduler.Default)
                    .Subscribe(async ik =>
                    {
                        try
                        {
                            await _mqttClient.PublishAsync(JsonConvert.SerializeObject(ik), "autono-xbox");
                        }
                        catch (Exception)
                        {
                           //
                        }
                    }));

                AddToLog($"Publishing Xbox events every {UpdateInterval}ms");
            }
            else
            {
                AddToLog($"xBox controller not connected");
            }

            if (!LinesEnabled)
            {
                _disposables.Add(_mqttClient.GetPublishByteObservable("autono-eye").ObserveOnDispatcher().Subscribe( 
                    bytes =>
                    {
                        ShellView.ImageSubject.OnNext(bytes.AsBuffer());
                    }));
            }
            else
            {
                _disposables.Add(_mqttClient.GetPublishByteObservable("opencv-cannylines").ObserveOnDispatcher().Subscribe(
                    bytes =>
                    {
                        ShellView.ImageSubject.OnNext(bytes.AsBuffer());
                    }));

                _disposables.Add(Observable.Interval(TimeSpan.FromMilliseconds(Convert.ToInt64(UpdateInterval)))
                    .SubscribeOn(NewThreadScheduler.Default)
                    .Subscribe(async _ =>
                    {
                        await _mqttClient.PublishAsync(JsonConvert.SerializeObject(new[] { CannyHigh, CannyLow }), "canny-lines-params");
                    }));
            }

            _disposables.Add(_mqttClient.GetPublishStringObservable("autono-sonar").ObserveOnDispatcher().Subscribe(r =>
                {
                    ShellView.Range = Convert.ToInt32(r);
                }));

            if (CirclesEnabled)
            {
                _disposables.Add(_mqttClient.GetPublishStringObservable("opencv-circle").ObserveOnDispatcher().Subscribe(r =>
                {
                    ShellView.OpenCvCircleDetectSubject.OnNext(JsonConvert.DeserializeObject<List<CircleF>>(r));
                }));

                _disposables.Add(Observable.Interval(TimeSpan.FromMilliseconds(Convert.ToInt64(UpdateInterval)))
                    .SubscribeOn(NewThreadScheduler.Default)
                    .Subscribe(async _ =>
                    {
                        var houghParams = new HoughCircleParams
                        {
                            Dp = HoughDp,
                            CannyThreshold = HoughCannyThreshold,
                            MaxCircles = HoughMaxCircles,
                            MinRadius = HoughMinRadius,
                            MinDistance = HoughMinDistance,
                            MaxRadius = HoughMaxRadius,
                            VotesThreshold = HoughVotesThreshold
                        };

                        await _mqttClient.PublishAsync(JsonConvert.SerializeObject(houghParams), "hough-circle-params");
                    }));

            }

            _disposables.Add(_mqttClient.GetPublishStringObservable("autono-resolutions").ObserveOnDispatcher().Subscribe(r =>
            {
                Resolutions = new BindableCollection<string>();

                foreach (var resolution in JsonConvert.DeserializeObject<List<string>>(r))
                {
                    Resolutions.Add(resolution);
                }
            }));
        }

        public IObservableCollection<string> Resolutions { get; set; } = new BindableCollection<string>();

        public async Task TextToSpeech(string text)
        {
            var t = TextForSpeech;

            if (!string.IsNullOrEmpty(text))
                t = text;

            using (var speech = new SpeechSynthesizer())
            {
                speech.Voice = SpeechSynthesizer.AllVoices.First(gender => gender.Gender == VoiceGender.Female);

                var stream = await speech.SynthesizeTextToStreamAsync(t);
                MediaElement.SetSource(stream, stream.ContentType);
                MediaElement.Play();
            }
        }

        public async Task BrokerConnect()
        {
            try
            {
                if (_mqttClient == null)
                {
                    await Connect();
                }
                else
                {
                    AddToLog("Disconnecting/reconnecting...");

                    try
                    {
                        foreach (var disposable in _disposables)
                            disposable.Dispose();
                    }
                    catch (Exception)
                    {
                        //
                    }

                    _mqttClient.Dispose();

                    _mqttClient = null;

                    await Connect();
                }
            }
            catch (Exception e)
            {
                AddToLog(e.Message);
            }
        }

        public async Task PublishMessage()
        {
            if (string.IsNullOrEmpty(PubMessage) || string.IsNullOrEmpty(PubTopic))
            {
                AddToLog("Please enter message and topic first");
                return;
            }

            await _mqttClient.PublishAsync(PubMessage, PubTopic);

            AddToLog("PublishAck");
        }

        private void AddToLog(string message)
        {
            Log.Insert(0, message);

            if (Log.Count > 1000)
                Log.RemoveAt(1000);
        }

        public void Subscribe()
        {
            if (string.IsNullOrEmpty(SubTopic))
            {
                Log.Insert(0, "Need a topic first");
                return;
            }

            _disposables.Add(_mqttClient.GetPublishStringObservable(SubTopic)
                .ObserveOnDispatcher()
                .Subscribe(AddToLog));

            _logger.Log(LogLevel.Info, $"Subscribed to {SubTopic}");
        }
    }
}