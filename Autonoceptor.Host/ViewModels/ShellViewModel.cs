using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Storage.Streams;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Autonoceptor.Service;
using Caliburn.Micro;
using Newtonsoft.Json;
using RxMqtt.Client;
using RxMqtt.Shared;

namespace Autonoceptor.Host.ViewModels
{
    public class ShellViewModel : Conductor<object>
    {
        private readonly Timer _timeoutTimer;
        private AutonoceptorService _autonoceptorService;
        private CancellationTokenSource _cameraCancellationTokenSource = new CancellationTokenSource();

        private CancellationTokenSource _carCancellationTokenSource = new CancellationTokenSource();

        private MqttClient _mqttClient;

        private IDisposable _startDisposable;
        private long _streamRunning;

        public ShellViewModel()
        {
            _timeoutTimer = new Timer(_ => TimeoutCallBack());

            _startDisposable = Observable.Timer(TimeSpan.FromSeconds(2))
                .ObserveOnDispatcher()
                .Subscribe(async _ => { await InitMqtt(); });

            Application.Current.Suspending += CurrentOnSuspending;
            Application.Current.Resuming += CurrentOnResuming;
        }

        public string BrokerIp { get; set; } = "172.16.0.246";

        public MediaElement MediaElement { get; } = new MediaElement();
        public int VideoProfile { get; set; } = 6; //6, 8

        public CaptureElement CaptureElement { get; set; } = new CaptureElement();

        private void TimeoutCallBack()
        {
            _carCancellationTokenSource?.Cancel();
        }

        private async Task InitMqtt()
        {
            _mqttClient?.Dispose();
            _mqttClient = null;

            _mqttClient = new MqttClient("autonoceptor-control", BrokerIp, 1883);
            var status = await _mqttClient.InitializeAsync();

            if (status != Status.Initialized)
                return;

            _mqttClient.GetPublishStringObservable("autono-heartbeat").ObserveOnDispatcher().Subscribe(
                _ =>
                {
                    if (_timeoutTimer == null)
                        return;

                    _timeoutTimer.Change((int) TimeSpan.FromSeconds(2).TotalMilliseconds, Timeout.Infinite);
                });

            _mqttClient.GetPublishStringObservable("autono-camera-start").ObserveOnDispatcher().Subscribe(
                async profileId =>
                {
                    if (!string.IsNullOrEmpty(profileId) && profileId.Length > 2)
                        return;

                    VideoProfile = Convert.ToInt32(profileId);

                    _cameraCancellationTokenSource?.Cancel(false);

                    await Task.Delay(1000);

                    _cameraCancellationTokenSource = new CancellationTokenSource();

#pragma warning disable 4014
                    StartStreamAsync();
#pragma warning restore 4014
                });

//            _mqttClient.GetPublishStringObservable("autono-car-start").ObserveOnDispatcher().Subscribe(
//                _ =>
//                {
//                    _timeoutTimer.Change((int) TimeSpan.FromSeconds(5).TotalMilliseconds, Timeout.Infinite);

//#pragma warning disable 4014
//                    InitializeAutonoceptor();
//#pragma warning restore 4014
//                });

            InitializeAutonoceptor();
        }

        private async void CurrentOnResuming(object sender, object o)
        {
            await InitMqtt();
        }

        private async void CurrentOnSuspending(object sender, SuspendingEventArgs suspendingEventArgs)
        {
            var deferral = suspendingEventArgs.SuspendingOperation.GetDeferral();

            _cameraCancellationTokenSource?.Cancel();
            _carCancellationTokenSource?.Cancel();
            _mqttClient?.Dispose();
            _mqttClient = null;

            await Task.Delay(1000);

            deferral.Complete();
        }

        private async Task InitializeAutonoceptor()
        {
            _carCancellationTokenSource?.Cancel(false);

            await Task.Delay(2000);

            _carCancellationTokenSource = new CancellationTokenSource();

            _autonoceptorService = new AutonoceptorService(BrokerIp);

            await _autonoceptorService.StartAsync(_carCancellationTokenSource.Token, _mqttClient);
        }

        private async Task<bool> StartStreamAsync()
        {
            if (Interlocked.Read(ref _streamRunning) == 1)
                return false;

            Interlocked.Exchange(ref _streamRunning, 1);

            var mediaCapture = new MediaCapture();

            await mediaCapture.InitializeAsync();

            var encodingProperties = mediaCapture.VideoDeviceController.GetAvailableMediaStreamProperties(MediaStreamType.VideoPreview).ToList();

            var res = new List<string>();

            for (var i = 0; i < encodingProperties.Count; i++)
            {
                var p = (VideoEncodingProperties) encodingProperties[i];
                res.Add($"{i}, {p.Width}x{p.Height}, {p.FrameRate.Numerator / p.FrameRate.Denominator} fps, {p.Subtype}");
            }

#pragma warning disable 4014
            _mqttClient.PublishAsync(JsonConvert.SerializeObject(res), "autono-resolutions");
#pragma warning restore 4014

            // set resolution
            await mediaCapture.VideoDeviceController.SetMediaStreamPropertiesAsync(MediaStreamType.VideoPreview, encodingProperties[Convert.ToInt32(VideoProfile)]); //2, 8, 9 - 60fps = better

            CaptureElement = new CaptureElement {Source = mediaCapture};

            await mediaCapture.StartPreviewAsync();

            var props = mediaCapture.VideoDeviceController.GetMediaStreamProperties(MediaStreamType.VideoPreview);

            await mediaCapture.SetEncodingPropertiesAsync(MediaStreamType.VideoPreview, props, null);

            while (!_cameraCancellationTokenSource.IsCancellationRequested)
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
                            await reader.LoadAsync((uint) stream.Size);
                            reader.ReadBytes(bytes);

                            if (_mqttClient == null)
                                continue;

                            try
                            {
                                await _mqttClient.PublishAsync(bytes, "autono-eye", TimeSpan.FromSeconds(3));
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
                }}

            Interlocked.Exchange(ref _streamRunning, 0);

            return false;
        }
    }
}