﻿using System;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.SerialCommunication;
using Windows.Storage.Streams;
using Autonoceptor.Shared.Imu;
using Autonoceptor.Shared.Utilities;
using Nito.AsyncEx;
using NLog;

namespace Autonoceptor.Service.Hardware
{
    public class Imu
    {
        private readonly ILogger _logger = LogManager.GetCurrentClassLogger();

        private SerialDevice _serialDevice;

        private DataReader _inputStream;

        private readonly Subject<ImuData> _subject = new Subject<ImuData>();

        private Task _readTask;

        private readonly CancellationToken _cancellationToken;

        private ImuData _currentImuData = new ImuData();

        private readonly AsyncLock _asyncLock = new AsyncLock();

        public async Task<ImuData> Get()
        {
            using (await _asyncLock.LockAsync())
            {
                return _currentImuData;
            }
        }

        public Imu(CancellationToken cancellationToken)
        {
            _cancellationToken = cancellationToken;
        }

        public async Task InitializeAsync()
        {
            _serialDevice = await SerialDeviceHelper.GetSerialDeviceAsync("DN01E099", 57600, TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20));

            if (_serialDevice == null)
                return;

            _logger.Log(LogLevel.Info, "Imu opened");

            _inputStream = new DataReader(_serialDevice.InputStream) { InputStreamOptions = InputStreamOptions.Partial };

            _readTask = new Task(async() =>
            {
                while (!_cancellationToken.IsCancellationRequested)
                {
                    var byteCount = await _inputStream.LoadAsync(32);

                    var buffer = new byte[byteCount];

                    _inputStream.ReadBytes(buffer);

                    var readings = Encoding.ASCII.GetString(buffer);

                    if (!readings.StartsWith("#") && !readings.EndsWith('\n'))
                    {
                        continue;
                    }

                    var yprReadings = readings.Replace("\r", "").Replace("\n", "").Replace("Y", "").Replace("P", "").Replace("R", "").Replace("=", "").Split('#');

                    foreach (var reading in yprReadings)
                    {
                        if (string.IsNullOrEmpty(reading))
                            continue;

                        try
                        {
                            var imuData = new ImuData();

                            var splitYpr = reading.Split(',');

                            if (splitYpr.Length != 3)
                                continue;

                            if (!double.TryParse(splitYpr[0], out var tempYaw))
                            {
                                continue;
                            }

                            if (tempYaw < 0)
                            {
                                var t = Math.Round(Math.Abs(tempYaw).Map(0, 180, 360, 181), 1);

                                imuData.Yaw = t;
                            }
                            else
                            {
                                imuData.Yaw = tempYaw;
                            }

                            double.TryParse(splitYpr[1], out var pitch);
                            imuData.Pitch = pitch;

                            double.TryParse(splitYpr[2], out var roll);
                            imuData.Roll = roll;

                            _subject.OnNext(imuData);

                            using (await _asyncLock.LockAsync())
                            {
                                _currentImuData = imuData;
                            }
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