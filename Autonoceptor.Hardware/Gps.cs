using System;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using Windows.Devices.SerialCommunication;
using Windows.Storage.Streams;
using Autonoceptor.Shared.Gps;
using Autonoceptor.Shared.Utilities;
using NLog;

namespace Autonoceptor.Hardware
{
    public class Gps
    {
        private readonly ILogger _logger = LogManager.GetCurrentClassLogger();

        private readonly ISubject<GpsFixData> _subject = new BehaviorSubject<GpsFixData>(null);

        private bool _disposed = true;

        private Task _gpsReadTask;

        private DataReader _inputStream;
        private DataWriter _outputStream;

        private SerialDevice _serialDevice;

        public async Task<GpsFixData> GetLatest()
        {
            return await _subject.ObserveOnDispatcher().Take(1);
        }

        public void Dispose()
        {
            _disposed = true;

            _serialDevice.Dispose();
            _serialDevice = null;

            _inputStream?.Dispose();
            _inputStream = null;

            _outputStream?.Dispose();
            _outputStream = null;

            _gpsReadTask?.Dispose();
            _gpsReadTask = null;

            _logger.Log(LogLevel.Info, "Gps disposed");
        }

        public async Task InitializeAsync()
        {
            if (!_disposed)
                return;

            _disposed = false;

            _serialDevice = await SerialDeviceHelper.GetSerialDeviceAsync("DN01E09J", 115200,
                TimeSpan.FromMilliseconds(25), TimeSpan.FromMilliseconds(25));

            if (_serialDevice == null)
                return;

            _logger.Log(LogLevel.Info, "Gps opened");

            _inputStream = new DataReader(_serialDevice.InputStream) {InputStreamOptions = InputStreamOptions.Partial};
            _outputStream = new DataWriter(_serialDevice.OutputStream);

            _gpsReadTask = new Task(async () =>
            {
                while (!_disposed)
                    try
                    {
                        if (_inputStream == null) break;

                        var byteCount = await _inputStream.LoadAsync(1128);
                        var sentences = _inputStream.ReadString(byteCount).Split('\n');

                        if (sentences.Length == 0)
                            continue;

                        var gpsFixData = new GpsFixData();

                        foreach (var sentence in sentences)
                        {
                            if (!sentence.StartsWith("$") || !sentence.EndsWith('\r'))
                                continue;

                            try
                            {
                                var tempData = GpsExtensions.ParseNmea(sentence);

                                if (tempData != null)
                                    gpsFixData = tempData;
                            }
                            catch (Exception e)
                            {
                                _logger.Log(LogLevel.Error, e);
                            }
                        }

                        if (gpsFixData.DateTime == DateTime.MinValue)
                            continue; //Most likely bad data, toss and continue

                        //The data is accumulative, so only need to publish once
                        _subject.OnNext(gpsFixData);
                    }
                    catch (Exception e)
                    {
                        _logger.Log(LogLevel.Error, e.Message);
                    }
            }, TaskCreationOptions.LongRunning);

            _gpsReadTask.Start();
        }

        public IObservable<GpsFixData> GetObservable()
        {
            return _subject.AsObservable();
        }
    }
}