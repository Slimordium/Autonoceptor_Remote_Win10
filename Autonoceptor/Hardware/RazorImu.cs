using System;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.SerialCommunication;
using Windows.Storage.Streams;
using Autonoceptor.Shared.Imu;
using NLog;

namespace Autonoceptor.Service.Hardware
{
    public class RazorImu
    {
        private ILogger _logger = LogManager.GetCurrentClassLogger();

        private SerialDevice _serialDevice;

        private DataReader _inputStream;

        private readonly Subject<ImuData> _subject = new Subject<ImuData>();

        private Task _readTask;

        private readonly CancellationToken _cancellationToken;

        private ImuData _currentImuData = new ImuData();

        public ImuData CurrentImuData
        {
            get => Volatile.Read(ref _currentImuData);
            set => Volatile.Write(ref _currentImuData, value);
        }

        public RazorImu(CancellationToken cancellationToken)
        {
            _cancellationToken = cancellationToken;
        }

        public async Task InitializeAsync()
        {
            _serialDevice = await SerialDeviceHelper.GetSerialDeviceAsync("DN01E099", 57600, TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20));

            if (_serialDevice == null)
                return;

            _inputStream = new DataReader(_serialDevice.InputStream) { InputStreamOptions = InputStreamOptions.Partial };

            _readTask = new Task(async() =>
            {
                while (!_cancellationToken.IsCancellationRequested)
                {
                    var byteCount = await _inputStream.LoadAsync(64);

                    var buffer = new byte[byteCount];

                    _inputStream.ReadBytes(buffer);

                    var readings = Encoding.ASCII.GetString(buffer);

                    var yprReadings = readings.Replace("\r", "").Replace("\n", "").Replace("Y", "").Replace("P", "").Replace("R", "").Replace("=", "").Split('#');

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

                            _subject.OnNext(imuData);

                            CurrentImuData = imuData;
                        }
                        catch (Exception e)
                        {
                            _logger.Log(LogLevel.Error, e.Message);
                        }
                    }
                }
            });
            _readTask.Start();
        }

        public IObservable<ImuData> GetReadObservable()
        {
            return _subject.AsObservable();
        }
    }
}