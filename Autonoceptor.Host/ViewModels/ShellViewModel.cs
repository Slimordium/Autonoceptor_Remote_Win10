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
            Observable.Timer(TimeSpan.FromSeconds(2))
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
                    Yaw = $"Yaw: {Convert.ToInt32(data.Yaw)}"; 
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
                    LatLon = data.ToString();
                    NotifyOfPropertyChange(nameof(LatLon));
                });
        }

        private IDisposable _yawDisposable;
        private IDisposable _odometerDisposable;

        public double TurnMagnitudeInputModifier { get; set; }

        public int CruiseControl { get; set; }

        private void SetNavParams()
        {
            _conductor.SetCruiseControl(CruiseControl);
            _conductor.Waypoints.SetSteerMagnitudeModifier(TurnMagnitudeInputModifier);
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

        public async Task GetDistanceHeading()
        {
            try
            {
                if (!Waypoints.Any())
                {
                    await ListWaypoints();
                }

                if (!Waypoints.Any())
                    return;

                var gpsFixData = await _conductor.Gps.GetLatest();

                var wp = _conductor.Waypoints.ActiveWaypoints[SelectedWaypoint];

                var distanceAndHeading = GpsExtensions.GetDistanceAndHeadingToWaypoint(gpsFixData.Lat, gpsFixData.Lon, wp.Lat, wp.Lon);

                await AddToLog($"Distance: {distanceAndHeading.DistanceInFeet}ft, Heading: {distanceAndHeading.HeadingToWaypoint}");
            }
            catch (Exception e)
            {
                await AddToLog(e.Message);
            }
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

        public async Task InitGps()
        {
            await _conductor.Gps.InitializeAsync().ConfigureAwait(false);
        }

        public async Task DisposeGps()
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

        public async Task ListWaypoints()
        {
            if (_conductor.Waypoints.Count == 0)
            {
                await AddToLog("No waypoints in list");
                return;
            }

            Waypoints = new BindableCollection<Waypoint>();

            Waypoints.AddRange(_conductor.Waypoints.ActiveWaypoints);

            NotifyOfPropertyChange(nameof(Waypoints));
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