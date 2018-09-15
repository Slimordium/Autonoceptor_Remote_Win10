using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.SerialCommunication;
using Windows.Storage.Streams;
using NLog;

namespace Autonoceptor.Hardware
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
            _odometerSet = (float)odoData.InTraveled;
        }

        private volatile float _odometerSet;

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
                var odoDataList = new List<OdometerData>();

                //TODO: send from ESP32 Thing as JSON, this is annoying. As long as it is fast enough...
                while (!_cancellationToken.IsCancellationRequested)
                {
                    while (odoDataList.Count < 3)
                    {
                        var byteCount = await _inputStream.LoadAsync(50);

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

                                if (split.Count < 2)
                                {
                                    continue;
                                }

                                if (!readString.Contains("IN=") && !readString.Contains("\r"))
                                {
                                    continue;
                                }

                                var odometerDataNew = new OdometerData();

                                if (!float.TryParse(split[0].Replace("IN=", "").Replace("\r", "").Replace("\n", ""), out var inches))
                                {
                                    odometerDataNew.InTraveled = lastOdometer.InTraveled;
                                }
                                else
                                {
                                    odometerDataNew.InTraveled = inches;
                                    lastOdometer.InTraveled = inches;
                                }

                                if (!float.TryParse(split[1].Replace("FPS=", ""), out var fps))
                                {
                                    odometerDataNew.FeetPerSecond = Math.Round(lastOdometer.FeetPerSecond, 1);
                                }
                                else
                                {
                                    odometerDataNew.FeetPerSecond = fps;
                                }

                                odometerDataNew.DistanceSinceSet = inches - _odometerSet;

                                odoDataList.Add(odometerDataNew);
                            }
                            catch (Exception e)
                            {
                                _logger.Log(LogLevel.Error, $"Odometer: {e.Message}");
                            }
                        }
                    }

                    var newData = new OdometerData
                    {
                        InTraveled = Math.Round(odoDataList.Average(d => d.InTraveled), 1),
                        FeetPerSecond = Math.Round(odoDataList.Average(d => d.FeetPerSecond), 1),
                        DistanceSinceSet = Math.Round(odoDataList.Average(d => d.DistanceSinceSet), 1)
                    };

                    _subject.OnNext(newData);

                    lastOdometer = newData;

                    odoDataList = new List<OdometerData>();
                }
            }, TaskCreationOptions.LongRunning);
            _readTask.Start();
        }

        public IObservable<OdometerData> GetObservable()
        {
            return _subject.AsObservable();
        }
    }

    public class OdometerData
    {
        public double InTraveled { get; set; }

        public double FeetPerSecond { get; set; }

        public double DistanceSinceSet { get; set; }

    }
}