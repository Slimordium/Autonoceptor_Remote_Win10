using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.SerialCommunication;
using Windows.Security.Cryptography.Core;
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
        private DataWriter _outputStream;

        private readonly Subject<ImuData> _subject = new Subject<ImuData>();

        private Task _readTask;

        private readonly CancellationToken _cancellationToken;

        private ImuData _currentImuData = new ImuData();

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

                    var lastYaw = -1d;

                    //while (imuReadings.Count < 2) //Not sure if we need this?
                    {
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

                                if (Math.Abs(yawDegrees - lastYaw) > 15 && lastYaw > -1)
                                {
                                    _logger.Log(LogLevel.Info, $"Skipped {yawDegrees} last {lastYaw}");
                                    continue;
                                }

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
                    }

                    try
                    {
                        if (!imuReadings.Any())
                            continue;

                        var avgYaw = imuReadings.Average(r => r.Yaw) - YawCorrection;
                        var avgPitch = imuReadings.Average(r => r.Pitch);
                        var avgRoll = imuReadings.Average(r => r.Roll);
                        var avgUncorrectedYaw = imuReadings.Average(r => r.UncorrectedYaw);

                        if (avgYaw < 0)
                            avgYaw += 360;

                        if (avgYaw > 360)
                            avgYaw -= 360;

                        lastYaw = avgYaw;

                        var avgImuData = new ImuData { Pitch = avgPitch, Yaw = avgYaw, Roll = avgRoll, UncorrectedYaw = avgUncorrectedYaw };

                        _subject.OnNext(avgImuData);
                    }
                    catch (Exception e)
                    {
                        _logger.Log(LogLevel.Error, $"{e.Message}");
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