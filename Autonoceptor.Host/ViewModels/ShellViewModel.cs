using System;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.ExtendedExecution;
using Windows.UI.Xaml;
using Caliburn.Micro;

namespace Autonoceptor.Host.ViewModels
{
    public class ShellViewModel : Conductor<object>
    {
        private readonly Conductor _conductor;
        
        private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();

        private ExtendedExecutionSession _session;

        private IDisposable _sessionDisposable;

        public ShellViewModel()
        {
            _conductor = new Conductor(BrokerIp);

            Application.Current.Suspending += CurrentOnSuspending;
            Application.Current.Resuming += CurrentOnResuming;

            _sessionDisposable = Observable.Interval(TimeSpan.FromMinutes(4)).Subscribe(async _ => { await RequestExtendedSession(); });

            Observable.Timer(TimeSpan.FromSeconds(5))
                .ObserveOnDispatcher()
                .Subscribe(async _ =>
                {
                    await StartConductor(); 
                });
        }

        private bool _started;

        public string BrokerIp { get; set; } = "172.16.0.246";

        //public int VideoProfile { get; set; } = 13; //60 = 320x240,30fps MJPG, 84 = 160x120, 30 fps, MJPG, "96, 800x600, 30 fps, MJPG" "108, 1280x720, 30 fps, MJPG"

        public async Task StartConductor()
        {
            if (_started)
                return;

            _started = true;

            await _conductor.InitializeAsync(_cancellationTokenSource);
        }

        private void CurrentOnResuming(object sender, object o)
        {
            _sessionDisposable?.Dispose();

            _sessionDisposable = Observable.Interval(TimeSpan.FromMinutes(4)).Subscribe(async _ => { await RequestExtendedSession(); });
        }

        private void NewSessionOnRevoked(object sender, ExtendedExecutionRevokedEventArgs args)
        {
            _session?.Dispose();
            _session = null;
        }

        private async Task RequestExtendedSession()
        {
            _session = new ExtendedExecutionSession { Reason = ExtendedExecutionReason.LocationTracking };

            _session.Revoked -= NewSessionOnRevoked;
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

            _cancellationTokenSource.Cancel();

            await Task.Delay(1000);

            deferral.Complete();
        }
    }
}