using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.ExtendedExecution;
using Windows.Devices.SerialCommunication;
using Windows.Storage.Streams;
using Windows.UI.Xaml;
using Autonoceptor.Hardware;
using Autonoceptor.Shared.Gps;
using Autonoceptor.Shared.Utilities;
using Autonoceptor.Vehicle;
using Caliburn.Micro;
using Hardware.Xbox;
using Hardware.Xbox.Enums;
using Newtonsoft.Json;
using Nito.AsyncEx;
using NLog.Targets.Rx;

namespace Autonoceptor.Host.ViewModels
{
    public class ShellViewModel : Conductor<object>
    {
        private readonly AsyncLock _asyncLock = new AsyncLock();

        private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();
        private readonly Conductor _conductor;

        private IDisposable _gpsDisposable;
        private IDisposable _lidarDisposable;
        private IDisposable _odometerDisposable;

        private ExtendedExecutionSession _session;

        private IDisposable _sessionDisposable;

        private bool _started;

        private IDisposable _yawDisposable;

        public ShellViewModel()
        {
            //_conductor = new Conductor(_cancellationTokenSource, BrokerIp);

            Application.Current.Suspending += CurrentOnSuspending;
            Application.Current.Resuming += CurrentOnResuming;

            _sessionDisposable = Observable.Interval(TimeSpan.FromMinutes(4))
                .Subscribe(async _ => { await RequestExtendedSession(); });

            //Automatically start...
            //Observable.Timer(TimeSpan.FromSeconds(2))
            //    .ObserveOnDispatcher()
            //    .Subscribe(async _ => { await StartConductor().ConfigureAwait(false); });

            //RxTarget.LogObservable.ObserveOnDispatcher().Subscribe(async e => { await AddToLog(e); });
        }

        public string Yaw { get; set; }

        public string LatLon { get; set; }

        public string Lidar { get; set; }

        public string OdometerIn { get; set; }

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

                return true;
            }
            set
            {
                if (_conductor != null)
                    _conductor.SpeedControlEnabled = value;
            }
        }

        //54383

        private SerialDevice _lcdSerialDevice;

        public double TurnMagnitudeInputModifier { get; set; } = 1; // LEAVE AT 1 NOT NEEDED

        public int CruiseControl { get; set; } = 340;

        private async Task AddToLog(string entry)
        {
            using (await _asyncLock.LockAsync())
            {
                Log.Insert(0, entry);

                if (Log.Count > 600)
                    Log.RemoveAt(598);
            }
        }

        private XboxDevice XboxDevice;

        private IDisposable _xboxDisposable;
        private IDisposable _xboxButtonDisposable;
        private IDisposable _xboxDpadDisposable;

        private DataWriter _outputStream;
        private DataReader _inputStream;

        private readonly AsyncLock _mutex = new AsyncLock();

        private async Task<string> Read()
        {
            //using (await _mutex.LockAsync())
            {
                _outputStream.WriteBytes(new[] { (byte)0x52 }); //Request new data frame
                var x = await _outputStream.StoreAsync();

                var inCount = await _inputStream.LoadAsync(140);

                if (inCount < 1)
                    return string.Empty;

                return _inputStream.ReadString(inCount);
            }
        }

        private async Task Write(byte[] buffer)
        {
            //using (await _mutex.LockAsync())
            {
                _outputStream.WriteBytes(buffer.ToArray());
                var x = await _outputStream.StoreAsync();
            }
        }

        public async Task StartConductor()
        {
            if (_started)
                return;

            _started = true;

            await AddToLog("Starting conductor");

            _lcdSerialDevice = await SerialDeviceHelper.GetSerialDeviceAsync("54383", 115200, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100));

            _inputStream = new DataReader(_lcdSerialDevice.InputStream);// { InputStreamOptions = InputStreamOptions.Partial };
            _outputStream = new DataWriter(_lcdSerialDevice.OutputStream);

            XboxDevice = new XboxDevice();
            await XboxDevice.InitializeAsync(CancellationToken.None);

            Observable.Interval(TimeSpan.FromMilliseconds(100)).ObserveOnDispatcher().Subscribe(async _ =>
            {
                var s = await Read();

                await AddToLog(s);

            });

            _xboxDisposable = XboxDevice.GetObservable()
                .Where(xboxData => xboxData != null)
                .Sample(TimeSpan.FromMilliseconds(40))
                .ObserveOnDispatcher()
                .Subscribe(async xboxData =>
                {
                    var moveMagnitude = 6000d; //Stopped
                    var steerMagnitude = 6000d; //Center

                    if (xboxData.RightTrigger > xboxData.LeftTrigger)
                    {
                        moveMagnitude = Math.Round(xboxData.RightTrigger.Map(0, 33000, 6000, 4400)); //Forward
                    }
                    else
                    {
                        moveMagnitude = Math.Round(xboxData.LeftTrigger.Map(0, 33000, 6000, 7500)); //Reverse
                    }

                    if (xboxData.RightStick.Direction == Direction.DownLeft ||
                        xboxData.RightStick.Direction == Direction.UpLeft ||
                        xboxData.RightStick.Direction == Direction.Left)
                    {
                        steerMagnitude = Math.Round(xboxData.RightStick.Magnitude.Map(0, 10000, 6000, 4000));
                    }

                    if (xboxData.RightStick.Direction == Direction.DownRight ||
                        xboxData.RightStick.Direction == Direction.UpRight ||
                        xboxData.RightStick.Direction == Direction.Right)
                    {
                        steerMagnitude = Math.Round(xboxData.RightStick.Magnitude.Map(0, 10000, 6000, 8000));
                    }

                    var moveMagBytesOut = moveMagnitude.ToString(CultureInfo.InvariantCulture).ToCharArray(0, 4).Select(c => (byte)c).ToList();
                    var steerMagBytesOut = steerMagnitude.ToString(CultureInfo.InvariantCulture).ToCharArray(0, 4).Select(c => (byte)c).ToList();
                    
                    var buffer = new List<byte> {0x02, 0x4D}; //STX - (M)ovement

                    buffer.AddRange(moveMagBytesOut);
                    buffer.AddRange(steerMagBytesOut);

                    await Write(buffer.ToArray());
                });


            //_xboxButtonDisposable = XboxDevice.GetObservable()
            //    .Where(xboxData => xboxData != null && xboxData.FunctionButtons.Any())
            //    .Sample(TimeSpan.FromMilliseconds(250))
            //    .ObserveOnDispatcher()
            //    .Subscribe(async xboxData =>
            //    {
            //        await OnNextXboxButtonData(xboxData);
            //    });

            //_xboxDpadDisposable = XboxDevice.GetObservable()
            //    .Where(xboxData => xboxData != null && xboxData.Dpad != Direction.None)
            //    .Sample(TimeSpan.FromMilliseconds(250))
            //    .ObserveOnDispatcher()
            //    .Subscribe(async xboxData =>
            //    {
            //        await OnNextXboxDpadData(xboxData);
            //    });

            if (_conductor == null)
                return;

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
                    OdometerIn = $"{odoData.FeetPerSecond} fps, {Math.Round(odoData.InTraveled / 12, 1)} ft, {Math.Round(odoData.InTraveled, 1)}in";
                    NotifyOfPropertyChange(nameof(OdometerIn));
                });

            _gpsDisposable = _conductor
                .Gps
                .GetObservable()
                .Where(data => data != null)
                .ObserveOnDispatcher()
                .Subscribe(data =>
                {
                    LatLon = data.ToString();
                    NotifyOfPropertyChange(nameof(LatLon));
                });

            _lidarDisposable = _conductor
                .Lidar
                .GetObservable()
                .Where(d => d != null)
                .Sample(TimeSpan.FromMilliseconds(100))
                .ObserveOnDispatcher()
                .Subscribe(data =>
                {
                    if (!data.IsValid)
                    {
                        //Lidar = "Invalid signal";
                    }
                    else
                    {
                        Lidar = $"Distance: {data.Distance}, Strength: {data.Strength}";
                    }

                    NotifyOfPropertyChange(nameof(Lidar));
                });
        }

        private void CurrentOnResuming(object sender, object o)
        {
            _sessionDisposable?.Dispose();

            _sessionDisposable = Observable
                .Interval(TimeSpan.FromMinutes(4))
                .Subscribe(async _ =>
                {
                    await RequestExtendedSession(); 
                });

            //Observable.Timer(TimeSpan
            //    .FromSeconds(3))
            //    .ObserveOnDispatcher()
            //    .Subscribe(async _ => { await StartConductor().ConfigureAwait(false); });
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
                if (!Waypoints.Any()) await ListWaypoints();

                if (!Waypoints.Any())
                    return;

                var gpsFixData = await _conductor.Gps.GetLatest();

                var wp = _conductor.Waypoints.CurrentWaypoint;

                if (wp == null)
                {
                    await AddToLog($"No waypoints in queue");
                    return;
                }

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
            //await _conductor.InitializeMqtt(BrokerIp);

            await StartConductor();
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
            Waypoints = new BindableCollection<Waypoint>();

            if (_conductor.Waypoints.Count == 0)
            {
                await AddToLog("No waypoints in list");
                NotifyOfPropertyChange(nameof(Waypoints));
                return;
            }

            Waypoints.AddRange(_conductor.Waypoints.ToArray());

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