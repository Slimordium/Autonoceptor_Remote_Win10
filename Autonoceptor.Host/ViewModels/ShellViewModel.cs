using System;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.ExtendedExecution;
using Windows.UI.Xaml;
using Caliburn.Micro;
using NLog;
using NLog.Targets.Rx;

namespace Autonoceptor.Host.ViewModels
{
    public class ShellViewModel : Conductor<object>
    {
        private readonly Conductor _conductor;
        
        private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();

        private ExtendedExecutionSession _session;

        public BindableCollection<string> Log { get; set; } = new BindableCollection<string>();

        private IDisposable _sessionDisposable;

        public ShellViewModel()
        {
            _conductor = new Conductor(_cancellationTokenSource, BrokerIp);

            Application.Current.Suspending += CurrentOnSuspending;
            Application.Current.Resuming += CurrentOnResuming;

            _sessionDisposable = Observable.Interval(TimeSpan.FromMinutes(4)).Subscribe(async _ => { await RequestExtendedSession(); });

            //Automatically start...
            Observable.Timer(TimeSpan.FromSeconds(5))
                .ObserveOnDispatcher()
                .Subscribe(async _ =>
                {
                    await StartConductor().ConfigureAwait(false);
                    
                    Log.Add("Initialized");
                });

            RxTarget.LogObservable.ObserveOnDispatcher().Subscribe(AddToLog);
        }

        private void AddToLog(string entry)
        {
            Log.Insert(0, entry);

            if (Log.Count > 600)
                Log.RemoveAt(599);
        }

        private bool _started;

        public string BrokerIp { get; set; } = "172.16.0.246";

        //public int VideoProfile { get; set; } = 13; //60 = 320x240,30fps MJPG, 84 = 160x120, 30 fps, MJPG, "96, 800x600, 30 fps, MJPG" "108, 1280x720, 30 fps, MJPG"

        public async Task StartConductor()
        {
            if (_started)
                return;

            _started = true;

            AddToLog("Starting conductor");

            await _conductor.InitializeAsync();
        }

        private void CurrentOnResuming(object sender, object o)
        {
            _sessionDisposable?.Dispose();

            _sessionDisposable = Observable.Interval(TimeSpan.FromMinutes(4)).Subscribe(async _ => { await RequestExtendedSession(); });

            Observable.Timer(TimeSpan.FromSeconds(5))
                .ObserveOnDispatcher()
                .Subscribe(async _ =>
                {
                    await StartConductor().ConfigureAwait(false);
                });
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

        public int GpsNavSpeed { get; set; } = 25;

        public int WpBoundryIn { get; set; } = 32;

        public void SetGpsNavSpeed()
        {
            _conductor.GpsNavMoveMagnitude = GpsNavSpeed;

            AddToLog($"Set GPS nav speed %{GpsNavSpeed}");
        }
            
        public void SetWpBoundry()
        {
            _conductor.WpTriggerDistance = WpBoundryIn;

            AddToLog($"WP Trigger distance {WpBoundryIn}in");
        }

        public void GetCurrentPosition()
        {
            AddToLog($"At Lat: {_conductor.CurrentLocation.Lat}, Lon: {_conductor.CurrentLocation.Lon}");
        }

        public void ListWaypoints()
        {
            var wps = _conductor.Waypoints;

            foreach (var gpsFixData in wps)
            {
                AddToLog($"Lat: {gpsFixData.Lat} Lon: {gpsFixData.Lon} - {gpsFixData.Quality}");
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