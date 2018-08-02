using System;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.SerialCommunication;
using Windows.Storage.Streams;

namespace Autonoceptor.Service.Hardware
{
    public class MaxbotixSonar
    {
        private SerialDevice _serialDevice;

        private DataReader _inputStream;

        public IObservable<int> SonarObservable { get; private set; }

        public async Task InitializeAsync()
        {
            _serialDevice = await SerialDeviceHelper.GetSerialDeviceAsync("104O", 9600, TimeSpan.FromMilliseconds(1000), TimeSpan.FromMilliseconds(1000));

            if (_serialDevice == null)
                return;

            _inputStream = new DataReader(_serialDevice.InputStream) { InputStreamOptions = InputStreamOptions.Partial };

            SonarObservable = GetObservable().ObserveOn(Scheduler.Default);
        }

        private IObservable<int> GetObservable()
        {
            var subject = new Subject<int>();

            Task.Run(async () =>
            {
                var byteCount = await _inputStream.LoadAsync(8);

                var buffer = new byte[byteCount];

                _inputStream.ReadBytes(buffer);

                var readings = Encoding.ASCII.GetString(buffer);

                var inches = readings.Split('|');

                foreach (var inch in inches)
                {
                    if (string.IsNullOrEmpty(inch) || !inch.StartsWith(">") || !inch.EndsWith("<"))
                        continue;

                    try
                    {
                        subject.OnNext(Convert.ToInt32(inch.Replace(">", "").Replace("<", "")));
                    }
                    catch (Exception e)
                    {
                        //
                    }
                }
            });


            return subject;
        }
    }
}