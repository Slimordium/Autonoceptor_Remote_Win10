using System;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.SerialCommunication;
using Windows.Storage.Streams;
using Autonoceptor.Shared.Gps;
using Autonoceptor.Shared.Utilities;

namespace Autonoceptor.Service.Hardware
{
    public class Gps
    {
        private SerialDevice _serialDevice;

        private DataReader _inputStream;
        private DataWriter _outputStream;

        private Task _gpsReadTask;

        private readonly Subject<GpsFixData> _subject = new Subject<GpsFixData>();

        public async Task InitializeAsync(CancellationToken cancellationToken)
        {
            _serialDevice = await SerialDeviceHelper.GetSerialDeviceAsync("DN01E09J", 115200, TimeSpan.FromMilliseconds(25), TimeSpan.FromMilliseconds(25));

            if (_serialDevice == null)
                return;

            _inputStream = new DataReader(_serialDevice.InputStream) { InputStreamOptions = InputStreamOptions.Partial };
            _outputStream = new DataWriter(_serialDevice.OutputStream);

            _gpsReadTask = new Task(async () =>
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var byteCount = await _inputStream.LoadAsync(1024);
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
                        catch (Exception)
                        {
                            //Yum
                        }
                    }

                    if (gpsFixData.DateTime == DateTime.MinValue)
                    {
                        continue; //Most likely bad data, toss and continue
                    }

                    //The data is accumulative, so only need to publish once
                    _subject.OnNext(gpsFixData);
                }
            });

            _gpsReadTask.Start();
        }

        public IObservable<GpsFixData> GetObservable()
        {
            return _subject.AsObservable();
        }
    }
}