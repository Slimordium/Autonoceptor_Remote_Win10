using System;
using System.IO;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.SerialCommunication;
using Windows.Storage.Streams;
using Autonoceptor.Shared.Imu;

namespace Autonoceptor.Service.Hardware
{
    public class RazorImu
    {
        private SerialDevice _serialDevice;

        private DataReader _inputStream;

        public async Task InitializeAsync()
        {
            _serialDevice = await SerialDeviceHelper.GetSerialDeviceAsync("N01E09J", 57600, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500));

            if (_serialDevice == null)
                return;

            _inputStream = new DataReader(_serialDevice.InputStream) { InputStreamOptions = InputStreamOptions.Partial };
        }

        public IObservable<ImuData> GetObservable(CancellationToken cancellationToken)
        {
            var imuDataSubject = new Subject<ImuData>();

            Task.Run(async () =>
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var byteCount = await _inputStream.LoadAsync(64);

                    var buffer = new byte[byteCount];

                    _inputStream.ReadBytes(buffer);

                    var readings = Encoding.ASCII.GetString(buffer);

                    var yprReadings = readings.Replace("\r", "").Replace("\n", "").Replace("Y", "").Replace("P", "")
                        .Replace("R", "").Replace("=", "").Split('#');

                    foreach (var reading in yprReadings)
                    {
                        if (string.IsNullOrEmpty(reading))
                            continue;

                        try
                        {
                            var splitYpr = reading.Split(',');

                            if (splitYpr.Length != 3)
                                continue;

                            if (!double.TryParse(splitYpr[0], out var yaw))
                                continue;

                            if (!double.TryParse(splitYpr[1], out var pitch))
                                continue;

                            if (!double.TryParse(splitYpr[2], out var roll))
                                continue;

                            var imuData = new ImuData
                            {
                                Yaw = yaw,
                                Pitch = pitch,
                                Roll = roll
                            };

                            imuDataSubject.OnNext(imuData);
                        }
                        catch (Exception e)
                        {
                            //
                        }
                    }
                }
            });

            return imuDataSubject.AsObservable();
        }
    }
}