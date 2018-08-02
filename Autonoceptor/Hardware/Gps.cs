using System;
using System.Collections.Async;
using System.Linq;
using System.Net.Sockets;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using System.Text;
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
        private SerialDevice _gpsSerialDevice;

        private DataReader _inputStream;
        private DataWriter _outputStream;

        public IObservable<GpsFixData> GpsFixObservable { get; private set; }

        public async Task InitializeAsync(CancellationToken cancellationToken)
        {
            _gpsSerialDevice = await SerialDeviceHelper.GetSerialDeviceAsync("105B", 9600, TimeSpan.FromMilliseconds(1500), TimeSpan.FromMilliseconds(1500));

            if (_gpsSerialDevice == null)
                return;

            _inputStream = new DataReader(_gpsSerialDevice.InputStream) {InputStreamOptions = InputStreamOptions.Partial};
            _outputStream = new DataWriter(_gpsSerialDevice.OutputStream);

            GpsFixObservable = GetObservable().ObserveOn(Scheduler.Default);
        }

        private IObservable<GpsFixData> GetObservable()
        {
            var dataObservable = new Subject<GpsFixData>();

            Task.Run(async () =>
            {
                var byteCount = await _inputStream.LoadAsync(1024);
                var bytes = new byte[byteCount];
                _inputStream.ReadBytes(bytes);

                var sentences = Encoding.ASCII.GetString(bytes).Split('\n');

                if (sentences.Length == 0)
                    return;

                foreach (var sentence in sentences)
                {
                    if (!sentence.StartsWith("$"))
                        continue;

                    var data = GpsExtensions.ParseNmea(sentence);

                    dataObservable.OnNext(data);
                }
            });

            return dataObservable.AsObservable();
        }

    }
}