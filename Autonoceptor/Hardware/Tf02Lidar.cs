using System;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.SerialCommunication;
using Windows.Storage.Streams;
using Autonoceptor.Shared;

namespace Autonoceptor.Service.Hardware
{
    public class Tf02Lidar
    {
        private SerialDevice _serialDevice;
        private DataReader _inputStream;
        private DataWriter _outputStream;

        public async Task InitializeAsync()
        {
            _serialDevice = await SerialDeviceHelper.GetSerialDeviceAsync("A105BLG5", 115200, TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20));

            if (_serialDevice == null)
                return;

            _inputStream = new DataReader(_serialDevice.InputStream) {InputStreamOptions = InputStreamOptions.Partial};
            _outputStream = new DataWriter(_serialDevice.OutputStream);
        }

        private Subject<LidarData> _subject;

        public IObservable<LidarData> GetObservable(CancellationToken cancellationToken)
        {
            if (_subject != null)
            {
                return _subject.AsObservable();
            }

            _subject = new Subject<LidarData>();

            Task.Run(async () =>
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var byteCount = await _inputStream.LoadAsync(8);
                    var bytes = new byte[byteCount];
                    _inputStream.ReadBytes(bytes);

                    var byteList = bytes.ToList();

                    var loc = byteList.IndexOf(0x59);

                    if (loc + 7 > byteList.Count)
                        continue;

                    if (bytes[0] != 0x59 && bytes[1] != 0x59)
                        continue;

                    var lidarData = new LidarData
                    {
                        Distance = (ushort) BitConverter.ToInt16(bytes, 2),
                        Strength = (ushort) BitConverter.ToInt16(bytes, 4),
                        Reliability = bytes[6]
                    };
                    
                    if (lidarData.Reliability <= 5 || lidarData.Reliability > 8) //If the value is a 7 or 8, it is reliable. Ignore the rest
                        continue;

                    _subject.OnNext(lidarData);
                }
            });

            return _subject.AsObservable();
        }
    }
}