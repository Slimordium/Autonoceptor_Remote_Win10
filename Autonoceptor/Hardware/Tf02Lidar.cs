using System;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.SerialCommunication;
using Windows.Storage.Streams;
using Autonoceptor.Shared;
using NLog;

namespace Autonoceptor.Service.Hardware
{
    public class Tf02Lidar
    {
        private SerialDevice _serialDevice;
        private DataReader _inputStream;
        private DataWriter _outputStream;
        private Task _lidarTask;
        private readonly Subject<LidarData> _subject = new Subject<LidarData>();

        private readonly CancellationToken _cancellationToken;

        private readonly ILogger _logger = LogManager.GetCurrentClassLogger();

        public Tf02Lidar(CancellationToken cancellationToken)
        {
            _cancellationToken = cancellationToken;
        }

        public async Task<LidarData> GetLatest()
        {
            return await _subject.ObserveOnDispatcher().Take(1);
        }

        public async Task InitializeAsync()
        {
            _serialDevice = await SerialDeviceHelper.GetSerialDeviceAsync("A105BLG5", 115200, TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20));

            if (_serialDevice == null)
                return;

            _logger.Log(LogLevel.Info, "Lidar opened");

            _inputStream = new DataReader(_serialDevice.InputStream) { InputStreamOptions = InputStreamOptions.Partial };
            _outputStream = new DataWriter(_serialDevice.OutputStream);

            _lidarTask = new Task(async () =>
            {
                while (!_cancellationToken.IsCancellationRequested)
                {
                    var lidarData = new LidarData();

                    try
                    {
                        var byteCount = await _inputStream.LoadAsync(8);

                        if (byteCount == 0)
                            continue;

                        var bytes = new byte[byteCount];
                        _inputStream.ReadBytes(bytes);

                        var byteList = bytes.ToList();

                        var loc = byteList.IndexOf(0x59);

                        if (loc + 7 > byteList.Count)
                            continue;

                        if (bytes[0] != 0x59 && bytes[1] != 0x59)
                            continue;

                        lidarData = new LidarData
                        {
                            Distance = (ushort) BitConverter.ToInt16(bytes, 2),
                            Strength = (ushort) BitConverter.ToInt16(bytes, 4),
                            Reliability = bytes[6]
                        };

                        if (lidarData.Reliability <= 5 || lidarData.Reliability > 8) //If the value is a 7 or 8, it is reliable. Ignore the rest
                        {
                            lidarData.IsValid = false;
                            continue;
                        }
                    }
                    catch (TimeoutException)
                    {
                        _logger.Log(LogLevel.Error, $"Lidar timed out");
                        lidarData.IsValid = false;
                    }
                    catch (Exception e)
                    {
                        _logger.Log(LogLevel.Error, $"Lidar error: {e.Message}");
                        lidarData.IsValid = false;
                    }

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