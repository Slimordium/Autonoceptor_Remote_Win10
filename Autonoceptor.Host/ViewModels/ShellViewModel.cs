using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.ExtendedExecution;
using Windows.UI.Xaml;
using Autonoceptor.Shared.Utilities;
using Caliburn.Micro;
using Nito.AsyncEx;
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

        public BindableCollection<Waypoint> Waypoints { get; set; } = new BindableCollection<Waypoint>();

        public int SelectedWaypoint { get; set; }

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

            RxTarget.LogObservable.ObserveOnDispatcher().Subscribe(async e => { await AddToLog(e);});
        }

        private readonly AsyncLock _asyncLock = new AsyncLock();


        private async Task AddToLog(string entry)
        {
            using (await _asyncLock.LockAsync())
            {
                Log.Insert(0, entry);

                if (Log.Count > 600)
                    Log.RemoveAt(598);
            }

        }

        private bool _started;

        public string BrokerIp { get; set; } = "172.16.0.246";

        //public int VideoProfile { get; set; } = 13; //60 = 320x240,30fps MJPG, 84 = 160x120, 30 fps, MJPG, "96, 800x600, 30 fps, MJPG" "108, 1280x720, 30 fps, MJPG"

        public async Task StartConductor()
        {
            if (_started)
                return;

            _started = true;

            await AddToLog("Starting conductor");

            await _conductor.InitializeAsync();
        }

        public async Task GetOdometerData()
        {
            var odoData = await _conductor.Odometer.GetOdometerData();

            await AddToLog($"P:{odoData.PulseCount} => {odoData.InTraveled}in, {odoData.CmTraveled}cm");
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

        public async Task SetGpsNavSpeed()
        {
            _conductor.GpsNavMoveMagnitude = GpsNavSpeed;

            await AddToLog($"Set GPS nav speed %{GpsNavSpeed}");
        }

        public async Task SetWpBoundry()
        {
            _conductor.WpTriggerDistance = WpBoundryIn;

            await AddToLog($"WP Trigger distance {WpBoundryIn}in");
        }

        public async Task CalibrateImu()
        {
            await _conductor.SyncImuToGpsHeading();
        }

        public async Task GetCurrentPosition()
        {
            var currentLocation = await _conductor.Gps.Get();

            await AddToLog($"At Lat: {currentLocation.Lat}, Lon: {currentLocation.Lon}, Heading: {currentLocation.Heading}");
        }

        public async Task GetYpr()
        {
            var currentImu = await _conductor.RazorImu.Get();

            await AddToLog($"Uncorrected Yaw: {currentImu.UncorrectedYaw}, Corrected Yaw: {currentImu.Yaw} Pitch: {currentImu.Pitch} Roll: {currentImu.Roll}");
        }

        public async Task GetHeadingDistanceToSelected()
        {
            var currentLocation = await _conductor.Gps.Get();

            var wp = _conductor.Waypoints[SelectedWaypoint];

            var distanceAndHeading = GpsExtensions.GetDistanceAndHeadingToDestination(currentLocation.Lat, currentLocation.Lon, wp.GpsFixData.Lat, wp.GpsFixData.Lon);

            await AddToLog($"Distance: {distanceAndHeading[0] / 12} ft, Heading: {distanceAndHeading[1]} degrees");

            var moveReq = await _conductor.GetMoveRequest(wp.GpsFixData, currentLocation);

            await AddToLog($"{moveReq.SteeringDirection} @ {moveReq.SteeringMagnitude}");
        }

        public async Task ListWaypoints()
        {
            var wps = _conductor.Waypoints;

            if (!wps.Any())
                await AddToLog("No waypoints in list");

            foreach (var waypoint in wps)
            {
                await AddToLog($"Lat: {waypoint.GpsFixData.Lat} Lon: {waypoint.GpsFixData.Lon} - {waypoint.GpsFixData.Quality}");
            }

            Waypoints = new BindableCollection<Waypoint>();

            Waypoints.AddRange(_conductor.Waypoints);

            NotifyOfPropertyChange("Waypoints");
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