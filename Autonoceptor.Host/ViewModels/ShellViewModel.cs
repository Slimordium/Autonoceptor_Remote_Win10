using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.ExtendedExecution;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Storage.Streams;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Autonoceptor.Service;
using Autonoceptor.Service.Hardware;
using Autonoceptor.Shared.Utilities;
using Caliburn.Micro;
using Hardware.Xbox;
using Hardware.Xbox.Enums;
using Newtonsoft.Json;
using RxMqtt.Client;
using RxMqtt.Shared;

namespace Autonoceptor.Host.ViewModels
{
    public class ShellViewModel : Conductor<object>
    {
        private readonly Conductor _conductor = new Conductor();
        private readonly Gps _gps = new Gps();
        private readonly Tf02Lidar _lidar = new Tf02Lidar();
        private readonly SparkFunSerial16X2Lcd _lcd = new SparkFunSerial16X2Lcd();

        private readonly Timer _timeoutTimer;

        private CancellationTokenSource _carCancellationTokenSource = new CancellationTokenSource();
        private CancellationTokenSource _videoStreamCancellationTokenSource = new CancellationTokenSource();

        private MqttClient _mqttClient;

        private List<IDisposable> _disposables = new List<IDisposable>();

        private IDisposable _remoteDisposable;

        private List<IDisposable> _sensorDisposables = new List<IDisposable>();

        private ExtendedExecutionSession _session;

        private bool _hwInitialized;

        public ShellViewModel()
        {
            _timeoutTimer = new Timer(_ => TimeoutCallBack());

            Application.Current.Suspending += CurrentOnSuspending;
            Application.Current.Resuming += CurrentOnResuming;

            _disposables.Add(Observable.Interval(TimeSpan.FromMinutes(4)).Subscribe(async _ => { await RequestExtendedSession(); }));
        }

        public string BrokerIp { get; set; } = "172.16.0.246";

        public MediaElement MediaElement { get; } = new MediaElement();
        public int VideoProfile { get; set; } = 13; //60 = 320x240,30fps MJPG, 84 = 160x120, 30 fps, MJPG, "96, 800x600, 30 fps, MJPG" "108, 1280x720, 30 fps, MJPG"

        public CaptureElement CaptureElement { get; set; } = new CaptureElement();

        private void TimeoutCallBack()
        {
            _carCancellationTokenSource?.Cancel();
        }

        private void CurrentOnResuming(object sender, object o)
        {
            _disposables.Add(Observable.Interval(TimeSpan.FromMinutes(4)).Subscribe(async _ => { await RequestExtendedSession(); }));

            //await InitializeAutonoceptor();
        }

        private void NewSessionOnRevoked(object sender, ExtendedExecutionRevokedEventArgs args)
        {
            _session?.Dispose();
            _session = null;
        }

        private async Task RequestExtendedSession()
        {
            _session = new ExtendedExecutionSession { Reason = ExtendedExecutionReason.LocationTracking };
            _session.Revoked += NewSessionOnRevoked;
            var sessionResult = await _session.RequestExtensionAsync();

            switch (sessionResult)
            {
                case ExtendedExecutionResult.Allowed:
                    //AddToLog("Session extended");
                    break;

                case ExtendedExecutionResult.Denied:
                    //AddToLog("Session extend denied");
                    break;
            }
        }

        private async void CurrentOnSuspending(object sender, SuspendingEventArgs suspendingEventArgs)
        {
            var deferral = suspendingEventArgs.SuspendingOperation.GetDeferral();

            foreach (var disposable in _disposables)
            {
                try
                {
                    disposable.Dispose();
                }
                catch (Exception)
                {
                    //
                }
            }

            _disposables = new List<IDisposable>();

            try
            {
                _videoStreamCancellationTokenSource?.Cancel();
                _videoStreamCancellationTokenSource = null;

                _carCancellationTokenSource?.Cancel();
                _carCancellationTokenSource = null;

                _mqttClient?.Dispose();
                _mqttClient = null;
            }
            catch (Exception e)
            {
                //
            }

            await Task.Delay(1000);

            deferral.Complete();
        }

        public async Task PublishSensorData()
        {
            if (!_hwInitialized)
            {
                await InitializeHardware();
            }

            if (_mqttClient == null)
            {
                _mqttClient?.Dispose();
                _mqttClient = null;

                _mqttClient = new MqttClient("autonoceptor-control", BrokerIp, 1883);
                var status = await _mqttClient.InitializeAsync();

                if (status != Status.Initialized)
                    return;
            }

            await ConnectToRemote();
          
            foreach (var sensorDisposable in _sensorDisposables)
            {
                sensorDisposable.Dispose();
            }

            _sensorDisposables = new List<IDisposable>();

            _sensorDisposables.Add(_gps.GetObservable(_carCancellationTokenSource.Token).ObserveOnDispatcher()
                .Where(fix => fix != null).Subscribe(async fix =>
                {
                    if (_mqttClient == null)
                        return;

                    await _mqttClient.PublishAsync(JsonConvert.SerializeObject(fix), "autono-gps");
                }));

            _sensorDisposables.Add(_lidar.GetObservable(_carCancellationTokenSource.Token).ObserveOnDispatcher()
                .Where(lidarData => lidarData != null).Sample(TimeSpan.FromMilliseconds(150)).Subscribe(
                    async lidarData =>
                    {
                        if (_mqttClient == null)
                            return;

                        await _mqttClient.PublishAsync(JsonConvert.SerializeObject(lidarData), "autono-lidar");
                    }));
        }

        public async Task ConnectToRemote()
        {
            if (_mqttClient == null)
            {
                _mqttClient?.Dispose();
                _mqttClient = null;

                _mqttClient = new MqttClient("autonoceptor-control", BrokerIp, 1883);
                var status = await _mqttClient.InitializeAsync();

                if (status != Status.Initialized)
                    return;
            }

            _remoteDisposable?.Dispose();

            _remoteDisposable = _mqttClient.GetPublishStringObservable("autono-xbox").ObserveOnDispatcher()
                .Subscribe(async serializedData =>
                {
                    if (string.IsNullOrEmpty(serializedData))
                        return;

                    try
                    {
                        var xboxData = JsonConvert.DeserializeObject<XboxData>(serializedData);

                        if (xboxData == null)
                            return;

                        await _conductor.OnNextXboxData(xboxData);
                    }
                    catch (Exception)
                    {
                        //
                    }
                        
                });
        }

        

        private async Task InitializeHardware()
        {
            _hwInitialized = true;

            await _lcd.InitializeAsync();
            await _gps.InitializeAsync();
            await _lidar.InitializeAsync();
            await _conductor.InitializeAsync(_carCancellationTokenSource);
        }

        private async Task<bool> StartVideoStreamAsync()
        {
            var mediaCapture = new MediaCapture();

            await mediaCapture.InitializeAsync();

            var encodingProperties = mediaCapture.VideoDeviceController
                .GetAvailableMediaStreamProperties(MediaStreamType.VideoPreview).ToList();

            var res = new List<string>();

            for (var i = 0; i < encodingProperties.Count; i++)
            {
                var p = (VideoEncodingProperties) encodingProperties[i];
                res.Add($"{i}, {p.Width}x{p.Height}, {p.FrameRate.Numerator / p.FrameRate.Denominator} fps, {p.Subtype}");
            }

            // set resolution
            await mediaCapture.VideoDeviceController.SetMediaStreamPropertiesAsync(MediaStreamType.VideoPreview, encodingProperties[Convert.ToInt32(VideoProfile)]); //2, 8, 9 - 60fps = better

            CaptureElement = new CaptureElement {Source = mediaCapture};

            await mediaCapture.StartPreviewAsync();

            var props = mediaCapture.VideoDeviceController.GetMediaStreamProperties(MediaStreamType.VideoPreview);

            await mediaCapture.SetEncodingPropertiesAsync(MediaStreamType.VideoPreview, props, null);

            while (!_videoStreamCancellationTokenSource.IsCancellationRequested)
            {
                using (var stream = new InMemoryRandomAccessStream())
                {
                    try
                    {
                        await mediaCapture.CapturePhotoToStreamAsync(ImageEncodingProperties.CreateJpeg(), stream);
                    }
                    catch (Exception)
                    {
                        await Task.Delay(1000);
                        continue;
                    }

                    try
                    {
                        using (var reader = new DataReader(stream.GetInputStreamAt(0)))
                        {
                            var bytes = new byte[stream.Size];
                            await reader.LoadAsync((uint)stream.Size);
                            reader.ReadBytes(bytes);

                            if (_mqttClient == null)
                                continue;

                            try
                            {
                                await _mqttClient.PublishAsync(bytes, "autono-eye", TimeSpan.FromSeconds(1));
                            }
                            catch (TimeoutException)
                            {
                                //
                            }
                        }
                    }
                    catch (Exception)
                    {
                        //
                    }
                }
            }

            return false;
        }
    }
}