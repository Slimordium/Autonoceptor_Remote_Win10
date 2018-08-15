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
        private SerialDevice _tf02SerialDevice;
        private DataReader _inputStream;
        private DataWriter _outputStream;

        public async Task InitializeAsync()
        {
            _tf02SerialDevice = await SerialDeviceHelper.GetSerialDeviceAsync("A105BLG5", 115200, TimeSpan.FromMilliseconds(1500), TimeSpan.FromMilliseconds(1500));

            if (_tf02SerialDevice == null)
                return;

            _inputStream = new DataReader(_tf02SerialDevice.InputStream);// {InputStreamOptions = InputStreamOptions.Partial};
            _outputStream = new DataWriter(_tf02SerialDevice.OutputStream);
        }

        public IObservable<LidarData> GetObservable(CancellationToken cancellationToken)
        {
            var dataObservable = new Subject<LidarData>();

            Task.Run(async () =>
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var byteCount = await _inputStream.LoadAsync(16);
                    var bytes = new byte[byteCount];
                    _inputStream.ReadBytes(bytes);

                    var byteList = bytes.ToList();

                    var loc = byteList.IndexOf(0x59);

                    if (loc + 7 > byteList.Count)
                        continue;

                    var lidarData = new LidarData();

                    var packet = byteList.GetRange(loc, 8);

                    lidarData.Distance = (ushort)BitConverter.ToInt16(packet.ToArray(), 2);
                    lidarData.Strength = (ushort)BitConverter.ToInt16(packet.ToArray(), 4);
                    lidarData.Reliability = packet.ToArray()[6];

                    if (lidarData.Reliability < 7) //If the value is a 7 or 8, it is reliable. Ignore the rest
                        continue;

                    dataObservable.OnNext(lidarData);
                }
            });

            return dataObservable.AsObservable();
        }
    }
}