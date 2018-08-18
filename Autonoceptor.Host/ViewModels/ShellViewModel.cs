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
        private readonly Conductor _conductor;
        


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

            _conductor = new Conductor(BrokerIp);

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

        public async Task StartConductor()
        {
            await _conductor.InitializeAsync(_carCancellationTokenSource);
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

        
    }
}