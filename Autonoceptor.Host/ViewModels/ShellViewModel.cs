﻿using System;
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
using Caliburn.Micro;
using Hardware.Xbox;
using Newtonsoft.Json;
using RxMqtt.Client;

namespace Autonoceptor.Host.ViewModels
{
    public class ShellViewModel : Conductor<object>
    {
        private readonly AutonoceptorController _autonoceptorController = new AutonoceptorController();

        private readonly Gps _gps = new Gps();
        private readonly Timer _timeoutTimer;
        private readonly Tf02Lidar _lidar = new Tf02Lidar();
        private readonly SparkFunSerial16X2Lcd _lcd = new SparkFunSerial16X2Lcd();

        private CancellationTokenSource _carCancellationTokenSource = new CancellationTokenSource();

        private MqttClient _mqttClient;

        private List<IDisposable> _disposables = new List<IDisposable>();

        public ShellViewModel()
        {
            _timeoutTimer = new Timer(_ => TimeoutCallBack());

            //_startDisposable = Observable.Timer(TimeSpan.FromSeconds(2))
            //    .ObserveOnDispatcher()
            //    .Subscribe(async _ => { await InitializeAutonoceptor(); });

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

        private async void CurrentOnResuming(object sender, object o)
        {
            await InitializeAutonoceptor();
        }

        private ExtendedExecutionSession _session;

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

            _carCancellationTokenSource?.Cancel();
            _carCancellationTokenSource = null;
            _mqttClient?.Dispose();
            _mqttClient = null;

            await Task.Delay(1000);

            deferral.Complete();
        }

        private async Task InitializeAutonoceptor()
        {
            _carCancellationTokenSource = new CancellationTokenSource();

            _mqttClient?.Dispose();
            _mqttClient = null;

            _mqttClient = new MqttClient("autonoceptor-control", BrokerIp, 1883);
            var status = await _mqttClient.InitializeAsync();

            await _lcd.InitializeAsync();
            await _gps.InitializeAsync();
            await _autonoceptorController.InitializeAsync(_carCancellationTokenSource.Token);
            await _lidar.InitializeAsync();

            _disposables.Add(_gps.GetObservable(_carCancellationTokenSource.Token).ObserveOnDispatcher()
                .Where(fix => fix != null).Subscribe(async fix =>
                {
                    if (_mqttClient == null)
                        return;

                    await _mqttClient.PublishAsync(JsonConvert.SerializeObject(fix), "autono-gps");
                }));

            //_heartBeatDisposable = _mqttClient.GetPublishStringObservable("autono-heartbeat").ObserveOnDispatcher().Subscribe(_ =>
            //    {
            //        _timeoutTimer.Change(TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
            //    });

            _disposables.Add(_lidar.GetObservable(_carCancellationTokenSource.Token).ObserveOnDispatcher()
                .Where(lidarData => lidarData != null).Subscribe(
                async lidarData =>
                {
                    if (_mqttClient == null)
                        return;

                    await _mqttClient.PublishAsync(JsonConvert.SerializeObject(lidarData), "autono-lidar");
                }));

            if (_mqttClient != null)
                _disposables.Add(_mqttClient.GetPublishStringObservable("autono-xbox").ObserveOnDispatcher()
                    .Subscribe(async s =>
                    {
                        if (_autonoceptorController == null || string.IsNullOrEmpty(s))
                            return;

                        var d = JsonConvert.DeserializeObject<XboxData>(s);

                        if (d == null)
                            return;

                        await _autonoceptorController.OnNextXboxData(d);
                    }));

            await Task.Delay(1000);

            //_timeoutTimer.Change(TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);

            Observable.Interval(TimeSpan.FromMinutes(5)).Subscribe(async _ => { await RequestExtendedSession(); });

            await StartStreamAsync();
        }

        private async Task<bool> StartStreamAsync()
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

            while (!_carCancellationTokenSource.IsCancellationRequested)
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