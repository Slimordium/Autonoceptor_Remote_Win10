using System;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.SerialCommunication;
using Windows.Storage.Streams;
using Nito.AsyncEx;
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

        private readonly AsyncLock _asyncLock = new AsyncLock();

        private OdometerData _odometerData = new OdometerData();

        public async Task<OdometerData> GetOdometerData()
        {
            using (await _asyncLock.LockAsync())
            {
                return _odometerData;
            }
        }

        public Odometer(CancellationToken cancellationToken)
        {
            _cancellationToken = cancellationToken;
        }

        public async Task InitializeAsync()
        {
            _serialDevice = await SerialDeviceHelper.GetSerialDeviceAsync("DN04Q28D", 115200, TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20));

            if (_serialDevice == null)
                return;

            _logger.Log(LogLevel.Info, "Odometer opened");

            _inputStream = new DataReader(_serialDevice.InputStream) { InputStreamOptions = InputStreamOptions.Partial };

            _readTask = new Task(async() =>
            {
                var lastOdometer = new OdometerData();

                //TODO: send from ESP32 Thing as JSON, this is annoying. As long as it is fast enough...
                while (!_cancellationToken.IsCancellationRequested)
                {
                    var byteCount = await _inputStream.LoadAsync(100);

                    var readString = _inputStream.ReadString(byteCount);

                    foreach (var ss in readString.Split('\n'))
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

                        try
                        {
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

                            _subject.OnNext(odometerDataNew);

                            using (await _asyncLock.LockAsync())
                            {
                                _odometerData = odometerDataNew;
                            }
                                
                        }
                        catch (Exception e)
                        {
                            _logger.Log(LogLevel.Error, $"{e.Message}");
                            _logger.Log(LogLevel.Error, readString);
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

    }
}