using System;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.SerialCommunication;
using Windows.Storage.Streams;
using NLog;

namespace Autonoceptor.Service.Hardware
{
    public class Odometer
    {
        private readonly ILogger _logger = LogManager.GetCurrentClassLogger();

        private SerialDevice _serialDevice;

        private DataReader _inputStream;

        private readonly Subject<OdometerData> _subject = new Subject<OdometerData>();

        private Task _readTask;

        private readonly CancellationToken _cancellationToken;

        public async Task<OdometerData> GetLatest()
        {
            return await _subject.ObserveOnDispatcher().Take(1);
        }

        public Odometer(CancellationToken cancellationToken)
        {
            _cancellationToken = cancellationToken;
        }

        //This will add distance in IN to the imu data from the time it was set.
        public async Task ZeroTripMeter()
        {
            var odoData = await GetLatest();
            _odometerSet = odoData.InTraveled;
        }

        private volatile float _previousIn;

        private volatile float _feetPerSecond;

        private IDisposable _fpsDisposable;

        private volatile float _odometerSet;

        public async Task InitializeAsync()
        {
            _serialDevice = await SerialDeviceHelper.GetSerialDeviceAsync("DN04Q28D", 115200, TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20));

            if (_serialDevice == null)
                return;

            _logger.Log(LogLevel.Info, "Odometer opened");

            _inputStream = new DataReader(_serialDevice.InputStream) { InputStreamOptions = InputStreamOptions.Partial };

            _fpsDisposable = Observable
                .Interval(TimeSpan.FromSeconds(1))
                .ObserveOnDispatcher()
                .Subscribe(async _ =>
                {
                    var inTraveled = (await GetLatest()).InTraveled;

                    var fps = (inTraveled - _previousIn) / 12;

                    _feetPerSecond = fps;

                    _previousIn = inTraveled;
                });

            _readTask = new Task(async() =>
            {
                var lastOdometer = new OdometerData();

                //TODO: send from ESP32 Thing as JSON, this is annoying. As long as it is fast enough...
                while (!_cancellationToken.IsCancellationRequested)
                {
                    var byteCount = await _inputStream.LoadAsync(100);

                    if (byteCount == 0)
                        continue;

                    var readString = _inputStream.ReadString(byteCount);

                    if (string.IsNullOrEmpty(readString))
                        continue;

                    foreach (var ss in readString.Split('\n'))
                    {
                        try
                        {
                            var split = ss.Split(',').ToList();

                            if (split.Count < 3)
                            {
                                continue;
                            }

                            if (!readString.Contains("P=") && !readString.Contains("\r"))
                            {
                                continue;
                            }
                        
                            var odometerDataNew = new OdometerData();

                            if (!float.TryParse(split[2].Replace("IN=", "").Replace("\r", "").Replace("\n", ""), out var inches))
                            {
                                odometerDataNew.InTraveled = lastOdometer.InTraveled;
                            }
                            else
                            {
                                odometerDataNew.InTraveled = inches;
                                lastOdometer.InTraveled = inches;
                            }

                            if (!float.TryParse(split[1].Replace("CM=", ""), out var cm))
                            {
                                odometerDataNew.CmTraveled = lastOdometer.CmTraveled;
                            }
                            else
                            {
                                odometerDataNew.CmTraveled = cm;
                                lastOdometer.CmTraveled = cm;
                            }

                            if (!int.TryParse(split[0].Replace("P=", ""), out var pulse))
                            {
                                odometerDataNew.PulseCount = lastOdometer.PulseCount;
                            }
                            else
                            {
                                odometerDataNew.PulseCount = pulse;
                                lastOdometer.PulseCount = pulse;
                            }

                            odometerDataNew.FeetPerSecond = _feetPerSecond;
                            odometerDataNew.DistanceSinceSet = inches - _odometerSet;

                            _subject.OnNext(odometerDataNew);
                        }
                        catch (Exception e)
                        {
                            _logger.Log(LogLevel.Error, $"Odometer: {e.Message}");
                        }
                    }
                }
            });
            _readTask.Start();
        }

        public IObservable<OdometerData> GetObservable()
        {
            return _subject.AsObservable();
        }
    }

    public class OdometerData
    {
        public int PulseCount { get; set; }

        public float CmTraveled { get; set; }

        public float InTraveled { get; set; }

        public float FeetPerSecond { get; set; }

        public float DistanceSinceSet { get; set; }

    }
}