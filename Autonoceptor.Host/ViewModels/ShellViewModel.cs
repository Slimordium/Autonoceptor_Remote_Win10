using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.ExtendedExecution;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Autonoceptor.Shared.Utilities;
using Caliburn.Micro;
using Nito.AsyncEx;
using NLog.Targets.Rx;

namespace Autonoceptor.Host.ViewModels
{
    public class ShellViewModel : Conductor<object>
    {
        private readonly AsyncLock _asyncLock = new AsyncLock();

        private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();
        private readonly Conductor _conductor;

        private ExtendedExecutionSession _session;

        private IDisposable _sessionDisposable;

        public string Yaw { get; set; }

        private bool _started;

        public string LatLon { get; set; }

        public string DistanceToWaypoint { get; set; }

        public string OdometerIn { get; set; }

        private IDisposable _gpsDisposable;

        public ShellViewModel()
        {
            _conductor = new Conductor(_cancellationTokenSource, BrokerIp);

            Application.Current.Suspending += CurrentOnSuspending;
            Application.Current.Resuming += CurrentOnResuming;

            _sessionDisposable = Observable.Interval(TimeSpan.FromMinutes(4))
                .Subscribe(async _ => { await RequestExtendedSession(); });

            //Automatically start...
            Observable.Timer(TimeSpan.FromSeconds(5))
                .ObserveOnDispatcher()
                .Subscribe(async _ => { await StartConductor().ConfigureAwait(false); });

            RxTarget.LogObservable.ObserveOnDispatcher().Subscribe(async e => { await AddToLog(e); });
        }

        public BindableCollection<string> Log { get; set; } = new BindableCollection<string>();

        public BindableCollection<Waypoint> Waypoints { get; set; } = new BindableCollection<Waypoint>();

        public int SelectedWaypoint { get; set; }

        public string BrokerIp { get; set; } = "172.16.0.246";

        public bool EnableNavSpeedControl
        {
            get
            {
                if (_conductor != null)
                    return _conductor.SpeedControlEnabled;

                return false;
            }
            set
            {
                if (_conductor != null)
                    _conductor.SpeedControlEnabled = value;
            }

        }

        private async Task AddToLog(string entry)
        {
            using (await _asyncLock.LockAsync())
            {
                Log.Insert(0, entry);

                if (Log.Count > 600)
                    Log.RemoveAt(598);
            }
        }

        public async Task StartConductor()
        {
            if (_started)
                return;

            _started = true;

            await AddToLog("Starting conductor");

            await _conductor.InitializeAsync();

            await Task.Delay(1000);

            _yawDisposable = _conductor
                .Imu
                .GetReadObservable()
                .ObserveOnDispatcher()
                .Subscribe(data =>
                {
                    Yaw = Convert.ToInt32(data.Yaw).ToString(); 
                    NotifyOfPropertyChange(nameof(Yaw));
                });

            _odometerDisposable = _conductor
                .Odometer
                .GetObservable()
                .ObserveOnDispatcher()
                .Subscribe(odoData =>
                {
                    OdometerIn = $"FPS: {odoData.FeetPerSecond}, Pulse: {odoData.PulseCount}, {odoData.InTraveled / 12} ft, {odoData.InTraveled}in";
                    NotifyOfPropertyChange(nameof(OdometerIn));
                });

            _gpsDisposable = _conductor
                .Gps
                .GetObservable()
                .ObserveOnDispatcher()
                .Subscribe(data =>
                {
                    //if (_conductor.Waypoints.Any())
                    //{
                    //    var distanceHeading = GpsExtensions.GetDistanceAndHeadingToWaypoint(data.Lat, data.Lon, Waypoints[SelectedWaypoint].GpsFixData.Lat, Waypoints[SelectedWaypoint].GpsFixData.Lon);

                    //    DistanceToWaypoint = $"{distanceHeading[0]} in., {distanceHeading[1]} degrees";
                    //}
                    //else
                    //{
                    //    DistanceToWaypoint = "No waypoints";
                    //}

                    LatLon = data.ToString();
                    NotifyOfPropertyChange(nameof(LatLon));
                    NotifyOfPropertyChange(nameof(DistanceToWaypoint));
                });
        }

        private IDisposable _yawDisposable;
        private IDisposable _odometerDisposable;

        public async Task GetOdometerData()
        {
            var odoData = await _conductor.Odometer.GetLatest();

            await AddToLog($"FPS: {odoData.FeetPerSecond}, Pulse: {odoData.PulseCount}, {odoData.InTraveled / 12} ft, {odoData.InTraveled}in");
        }

        private void CurrentOnResuming(object sender, object o)
        {
            _sessionDisposable?.Dispose();

            _sessionDisposable = Observable.Interval(TimeSpan.FromMinutes(4))
                .Subscribe(async _ => { await RequestExtendedSession(); });

            Observable.Timer(TimeSpan.FromSeconds(5))
                .ObserveOnDispatcher()
                .Subscribe(async _ => { await StartConductor().ConfigureAwait(false); });
        }

        private void NewSessionOnRevoked(object sender, ExtendedExecutionRevokedEventArgs args)
        {
            _session?.Dispose();
            _session = null;
        }

        public async Task InitMqtt()
        {
            await _conductor.InitializeMqtt(BrokerIp);
        }

        private async Task RequestExtendedSession()
        {
            _session = new ExtendedExecutionSession {Reason = ExtendedExecutionReason.LocationTracking};

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

        public async Task SetGpsNavSpeed()
        {
            await _conductor.Gps.InitializeAsync().ConfigureAwait(false);
        }

        public async Task SetWpBoundry()
        {
            try
            {
                _conductor.Gps.Dispose();
            }
            catch (Exception e)
            {
                await AddToLog(e.Message);
            }
        }

        public void CalibrateImu()
        {
            _conductor.SyncImuYaw();
        }

        public async Task GetCurrentPosition()
        {
            var currentLocation = await _conductor.Gps.GetLatest();

            await AddToLog($"At Lat: {currentLocation.Lat}, Lon: {currentLocation.Lon}, Heading: {currentLocation.Heading}");
        }

        public async Task GetYpr()
        {
            var currentImu = await _conductor.Imu.GetLatest();

            await AddToLog($"Yaw: {currentImu.Yaw} Pitch: {currentImu.Pitch} Roll: {currentImu.Roll}");
        }

        public async Task GetHeadingDistanceToSelected()
        {
            var currentLocation = await _conductor.Gps.GetLatest();

            var wp = _conductor.Waypoints[SelectedWaypoint];

            var distanceAndHeading = GpsExtensions.GetDistanceAndHeadingToWaypoint(currentLocation.Lat,
                currentLocation.Lon, wp.GpsFixData.Lat, wp.GpsFixData.Lon);

            await AddToLog($"Distance: {distanceAndHeading.DistanceInFeet} ft, Heading: {distanceAndHeading.HeadingToWaypoint} degrees");
        }

        public async Task ListWaypoints()
        {
            var wps = _conductor.Waypoints;

            if (!wps.Any())
                await AddToLog("No waypoints in list");

            foreach (var waypoint in wps)
                await AddToLog(
                    $"Lat: {waypoint.GpsFixData.Lat} Lon: {waypoint.GpsFixData.Lon} - {waypoint.GpsFixData.Quality}");

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