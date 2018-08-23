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
        private Task _lidarTask;
        private readonly Subject<LidarData> _subject = new Subject<LidarData>();

        public async Task InitializeAsync(CancellationToken cancellationToken)
        {
            _serialDevice = await SerialDeviceHelper.GetSerialDeviceAsync("A105BLG5", 115200, TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20));

            if (_serialDevice == null)
                return;

            _inputStream = new DataReader(_serialDevice.InputStream) { InputStreamOptions = InputStreamOptions.Partial };
            _outputStream = new DataWriter(_serialDevice.OutputStream);

            _lidarTask = new Task(async () =>
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
                        Distance = (ushort)BitConverter.ToInt16(bytes, 2),
                        Strength = (ushort)BitConverter.ToInt16(bytes, 4),
                        Reliability = bytes[6]
                    };

                    if (lidarData.Reliability <= 5 || lidarData.Reliability > 8) //If the value is a 7 or 8, it is reliable. Ignore the rest
                        continue;

                    _subject.OnNext(lidarData);
                }
            });
            _lidarTask.Start();
        }

        public IObservable<LidarData> GetObservable()
        {
            return _subject.AsObservable();
        }
    }
}