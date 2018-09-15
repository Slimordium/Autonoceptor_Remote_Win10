using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.SerialCommunication;
using Windows.Storage.Streams;
using Autonoceptor.Shared.Imu;
using NLog;

namespace Autonoceptor.Hardware
{
    /// <summary>
    /// SparkFun Razor IMU
    /// With this firmware: https://github.com/Razor-AHRS/razor-9dof-ahrs/wiki/tutorial
    /// </summary>
    public class Imu
    {
        private readonly ILogger _logger = LogManager.GetCurrentClassLogger();

        private SerialDevice _serialDevice;

        private DataReader _inputStream;
        private DataWriter _outputStream;

        private readonly Subject<ImuData> _subject = new Subject<ImuData>();

        private Task _readTask;

        private readonly CancellationToken _cancellationToken;

        public Imu(CancellationToken cancellationToken)
        {
            _cancellationToken = cancellationToken;
        }

        private static double _yawCorrection;

        public double YawCorrection
        {
            get => Volatile.Read(ref _yawCorrection);
            set => Volatile.Write(ref _yawCorrection, value);
        }

        public async Task<ImuData> GetLatest()
        {
            return await _subject.ObserveOnDispatcher().Take(1);
        }

        public async Task InitializeAsync()
        {
            _serialDevice = await SerialDeviceHelper.GetSerialDeviceAsync("DN01E099", 38400, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(500));

            if (_serialDevice == null)
                return;

            _logger.Log(LogLevel.Info, "Imu opened");

            _readTask = new Task(async () =>
            {
                _outputStream = new DataWriter(_serialDevice.OutputStream);
                _inputStream = new DataReader(_serialDevice.InputStream) { InputStreamOptions = InputStreamOptions.Partial };

                for (var i = 0; i < 3; i++) //This usually does not work the 1st time...
                {
                    await Task.Delay(500);
                    _outputStream.WriteBytes(new[] { (byte)'#', (byte)'o', (byte)'0' }); //Set to pull frame instead of stream
                    await _outputStream.StoreAsync();
                }
                
                await Task.Delay(500);

                while (!_cancellationToken.IsCancellationRequested)
                {
                    var imuReadings = new List<ImuData>();

                    _outputStream.WriteBytes(new[] { (byte)'#', (byte)'f' }); //Request next data frame
                    await _outputStream.StoreAsync();

                    await _inputStream.LoadAsync(32);

                    var buffer = new byte[_inputStream.UnconsumedBufferLength];

                    _inputStream.ReadBytes(buffer);

                    var imuReadString = Encoding.ASCII.GetString(buffer);

                    var readings = imuReadString.Split("#");

                    foreach (var data in readings)
                    {
                        if (!data.StartsWith("Y") && !data.EndsWith('\n'))
                            continue;

                        var reading = data.Replace("\r\n", "").Replace("#", "").Replace("YPR=", "");

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

                            var yawDegrees = tempYaw;

                            yawDegrees = yawDegrees % 360;

                            if (yawDegrees < 0)
                                yawDegrees += 360;

                            imuData.UncorrectedYaw = yawDegrees;
                            imuData.Yaw = yawDegrees;

                            double.TryParse(splitYpr[1], out var pitch);
                            imuData.Pitch = pitch;

                            double.TryParse(splitYpr[2], out var roll);
                            imuData.Roll = roll;

                            imuReadings.Add(imuData);
                        }
                        catch (Exception e)
                        {
                            _logger.Log(LogLevel.Error, e.Message);
                        }
                    }

                    try
                    {
                        if (!imuReadings.Any())
                            continue;

                        var avgYaw = Math.Round(imuReadings.Average(r => r.Yaw) - YawCorrection, 1);
                        var avgPitch = Math.Round(imuReadings.Average(r => r.Pitch), 1);
                        var avgRoll = Math.Round(imuReadings.Average(r => r.Roll), 1);
                        var avgUncorrectedYaw = Math.Round(imuReadings.Average(r => r.UncorrectedYaw), 1);

                        if (avgYaw < 0)
                            avgYaw += 360;

                        if (avgYaw > 360)
                            avgYaw -= 360;

                        var avgImuData = new ImuData { Pitch = avgPitch, Yaw = avgYaw, Roll = avgRoll, UncorrectedYaw = avgUncorrectedYaw };

                        _subject.OnNext(avgImuData);
                    }
                    catch (Exception e)
                    {
                        _logger.Log(LogLevel.Error, $"{e.Message}");
                    }
                }
            }, TaskCreationOptions.LongRunning);
            _readTask.Start();
        }

        public IObservable<ImuData> GetReadObservable()
        {
            return _subject.AsObservable();
        }
    }
}