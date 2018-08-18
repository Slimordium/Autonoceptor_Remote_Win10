using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.UI.Xaml.Controls;
using Autonoceptor.Service.Hardware;
using Autonoceptor.Shared.Gps;
using Autonoceptor.Shared.Utilities;
using Hardware.Xbox;
using Hardware.Xbox.Enums;
using Newtonsoft.Json;
using RxMqtt.Client;
using RxMqtt.Shared;

namespace Autonoceptor.Host
{
    public class Conductor
    {
        private readonly MaestroPwmController _maestroPwmController = new MaestroPwmController();
        private CancellationToken _cancellationToken;

        private CancellationTokenSource _remoteTokenSource = new CancellationTokenSource();

        public Gps Gps { get; } = new Gps();

        private int _rightMax = 1800;
        private int _center = 1166;
        private int _leftMax = 816;

        private int _forwardMax = 1800;
        private int _stopped = 1500;
        private int _reverseMax = 1090;

        private ushort _movementChannel = 0;
        private ushort _steeringChannel = 1;

        private ushort _navEnableChannel = 12;
        private ushort _recordWaypointsChannel = 13;
        private ushort _enableRemoteChannel = 14;

        private readonly string _brokerIp;

        private IDisposable _remoteDisposable;

        private int _lidarRange;
        private bool _warn;
        private bool _emergencyStopped;

        private MqttClient _mqttClient;

        private readonly List<IDisposable> _disposables = new List<IDisposable>();

        private bool _recordWaypoints;

        private StorageFolder _waypointFolder = ApplicationData.Current.LocalCacheFolder;

        private List<IDisposable> _sensorDisposables = new List<IDisposable>();

        private readonly Tf02Lidar _lidar = new Tf02Lidar();
        private readonly SparkFunSerial16X2Lcd _lcd = new SparkFunSerial16X2Lcd();

        private string _waypointFile;// = await storageFolder.CreateFileAsync($"waypoints-{DateTime.Now:MMM-dd-HH:mm:ss.ff}.txt", CreationCollisionOption.OpenIfExists);

        private GpsFixData _gpsFixData = new GpsFixData();

        public async Task InitializeAsync(CancellationTokenSource cancellationTokenSource)
        {
            _cancellationToken = cancellationTokenSource.Token;

            await Task.WhenAll(
                Gps.InitializeAsync(),
                _lidar.InitializeAsync(),
                _lcd.InitializeAsync(),
                _maestroPwmController.InitializeAsync(_cancellationToken)
            );

            cancellationTokenSource.Token.Register(async () => { await Stop(); });

            if (Gps != null)
            {
                //Write GPS fix data to file, if switch is closed. Gps publishes fix data once a second
                _disposables.Add(Gps.GetObservable(_cancellationToken)
                    .ObserveOnDispatcher()
                    .Subscribe(async gpsFixData =>
                    {
                        if (_waypointFile == null || !_recordWaypoints || (gpsFixData.Lat == _gpsFixData.Lat && gpsFixData.Lon == _gpsFixData.Lon))
                            return;

                        _gpsFixData = gpsFixData;

                        try
                        {
                            await FileExtensions.SaveStringToFile(_waypointFile, gpsFixData.ToString());
                        }
                        catch (Exception)
                        {
                            //
                        }
                    }));
            }

            if (_lidar != null)
            {
                _disposables.Add(_lidar.GetObservable(_cancellationToken)
                    .ObserveOnDispatcher()
                    .Subscribe(async rangeData =>
                    {
                        await UpdateLidarRange(rangeData.Distance); 
                    }));
            }

            if (_maestroPwmController == null)
                return;

            _disposables.Add(_maestroPwmController.GetDigitalChannelObservable(_navEnableChannel, TimeSpan.FromMilliseconds(200))
                .ObserveOnDispatcher()
                .Subscribe(
                isSet =>
                {
                    //TODO: This should follow stored waypoints
                }));

            //If the enable remote switch is closed, start streaming video/sensor data
            _disposables.Add(_maestroPwmController.GetDigitalChannelObservable(_enableRemoteChannel, TimeSpan.FromMilliseconds(500))
                .ObserveOnDispatcher()
                .Subscribe(
                async isSet =>
                {
                    if (isSet)
                    {
                        _remoteTokenSource = new CancellationTokenSource();

                        await Task.WhenAll(StartVideoStreamAsync(), PublishSensorData());
                    }
                    else
                    {
                        _remoteTokenSource.Cancel();

                        _remoteDisposable.Dispose();

                        foreach (var sensor in _sensorDisposables)
                        {
                            sensor.Dispose();
                        }

                        _sensorDisposables = new List<IDisposable>();

                        _mqttClient.Dispose();
                        _mqttClient = null;
                    }
                    
                }));

            //Set _recordWaypoints to "true" if the channel is pulled high
            _disposables.Add(_maestroPwmController.GetDigitalChannelObservable(_recordWaypointsChannel, TimeSpan.FromMilliseconds(500))
                .ObserveOnDispatcher()
                .Subscribe(
                isSet =>
                {
                    if (isSet)
                    {
                        _recordWaypoints = true;

                        if (string.IsNullOrEmpty(_waypointFile))
                        {
                            try
                            {
                                _waypointFile = $"waypoints-{DateTime.Now:MMM-dd-HH-mm-ss-ff}.txt";
                            }
                            catch (Exception)
                            {
                                //
                            }
                        }

                        return;
                    }

                    _recordWaypoints = false;
                    _waypointFile = null;
                }));
        }

        public Conductor(string brokerIp)
        {
            _brokerIp = brokerIp;
        }

        private async Task<bool> InitMqtt()
        {
            if (_mqttClient == null)
            {
                _mqttClient?.Dispose();
                _mqttClient = null;

                _mqttClient = new MqttClient("autonoceptor-control", _brokerIp, 1883);
                var status = await _mqttClient.InitializeAsync();

                if (status != Status.Initialized)
                    return false;
            }

            return true;
        }

        public async Task ConnectToRemote()
        {
            if (!await InitMqtt())
                return;

            _remoteDisposable?.Dispose();

            _remoteDisposable = _mqttClient.GetPublishStringObservable("autono-xbox")
                .ObserveOnDispatcher()
                .Subscribe(async serializedData =>
                {
                    if (string.IsNullOrEmpty(serializedData))
                        return;

                    try
                    {
                        var xboxData = JsonConvert.DeserializeObject<XboxData>(serializedData);

                        if (xboxData == null)
                            return;

                        await OnNextXboxData(xboxData);
                    }
                    catch (Exception)
                    {
                        //
                    }

                });

            await StartVideoStreamAsync();
        }

        public async Task PublishSensorData()
        {
            if (!await InitMqtt())
                return;

            foreach (var sensorDisposable in _sensorDisposables)
            {
                sensorDisposable.Dispose();
            }

            _sensorDisposables = new List<IDisposable>();

            _sensorDisposables.Add(Gps.GetObservable(_cancellationToken).ObserveOnDispatcher()
                .Where(fix => fix != null).Subscribe(async fix =>
                {
                    if (_mqttClient == null)
                        return;

                    try
                    {
                        await _mqttClient.PublishAsync(JsonConvert.SerializeObject(fix), "autono-gps");
                    }
                    catch (Exception)
                    {
                        //
                    }

                }));

            _sensorDisposables.Add(_lidar.GetObservable(_cancellationToken).ObserveOnDispatcher()
                .Where(lidarData => lidarData != null).Sample(TimeSpan.FromMilliseconds(150)).Subscribe(
                    async lidarData =>
                    {
                        if (_mqttClient == null)
                            return;

                        try
                        {
                            await _mqttClient.PublishAsync(JsonConvert.SerializeObject(lidarData), "autono-lidar");
                        }
                        catch (Exception)
                        {
                            //
                        }

                        
                    }));
        }

        public async Task UpdateLidarRange(int range)
        {
            _lidarRange = range;

            if (range <= 90)
            {
                await EmergencyStop();
            }

            if (range > 90 && range <= 130)
            {
                await EmergencyStop(true);

                _warn = true;
            }

            if (range > 130)
            {
                await EmergencyStop(true);

                _warn = false;
            }
        }

        private async Task Stop()
        {
            await Task.WhenAll(new[]
            {
                _maestroPwmController.SetChannelValue(_stopped, _movementChannel),
                _maestroPwmController.SetChannelValue(_center, _steeringChannel)
            });
        }


        /// <summary>
        /// Valid for channels 12 - 23
        /// </summary>
        /// <param name="channel"></param>
        /// <returns></returns>
        public async Task<bool> GetDigitalChannelState(ushort channel)
        {
            if (_maestroPwmController == null)
                return false;

            var value = await _maestroPwmController.GetChannelValue(channel);

            return value > 200;
        }

        /// <summary>
        /// Valid for channels 0 - 12
        /// </summary>
        /// <param name="channel"></param>
        /// <returns></returns>
        public async Task<int> GetAnalogChannelState(ushort channel)
        {
            if (_maestroPwmController == null)
                return 0;

            var value = await _maestroPwmController.GetChannelValue(channel);

            return value;
        }

        public async Task EmergencyStop(bool isCanceled = false)
        {
            if (isCanceled)
            {
                _emergencyStopped = false;

                return;
            }

            _emergencyStopped = true;

            await Stop();
        }

        public async Task MoveRequest(MoveRequest request)
        {
            if (_maestroPwmController == null || _cancellationToken.IsCancellationRequested || _emergencyStopped)
                return;

            var moveValue = _stopped;
            var steerValue = _center;

            switch (request.MovementDirection)
            {
                case MovementDirection.Forward:
                    moveValue = Convert.ToUInt16(request.MovementMagnitude.Map(0, 100, _stopped, _forwardMax)) * 4;
                    break;
                case MovementDirection.Reverse:
                    moveValue = Convert.ToUInt16(request.MovementMagnitude.Map(0, 100, _stopped, _reverseMax)) * 4;
                    break;
            }

            if (_warn)
            {
                moveValue = moveValue / 2;
            }

            switch (request.SteeringDirection)
            {
                case SteeringDirection.Left:
                    steerValue = Convert.ToUInt16(request.SteeringMagnitude.Map(0, 100, _center, _leftMax)) * 4;
                    break;
                case SteeringDirection.Right:
                    steerValue = Convert.ToUInt16(request.SteeringMagnitude.Map(0, 100, _center, _rightMax)) * 4;
                    break;
            }

            await Task.WhenAll(new[]
            {
                _maestroPwmController.SetChannelValue(moveValue, _movementChannel),
                _maestroPwmController.SetChannelValue(steerValue, _steeringChannel)
            });
        }

        public async Task OnNextXboxData(XboxData xboxData)
        {
            if (_maestroPwmController == null || _cancellationToken.IsCancellationRequested || _emergencyStopped)
                return;

            var direction = _center;

            switch (xboxData.RightStick.Direction)
            {
                case Direction.UpLeft:
                case Direction.DownLeft:
                case Direction.Left:
                    direction = Convert.ToUInt16(xboxData.RightStick.Magnitude.Map(0, 10000, _center, _leftMax)) * 4;
                    break;
                case Direction.UpRight:
                case Direction.DownRight:
                case Direction.Right:
                    direction = Convert.ToUInt16(xboxData.RightStick.Magnitude.Map(0, 10000, _center, _rightMax)) * 4;
                    break;
            }

            await _maestroPwmController.SetChannelValue(direction, _steeringChannel); //Channel 1 is Steering

            var forwardMagnitude = Convert.ToUInt16(xboxData.LeftTrigger.Map(0, 33000, _stopped, _forwardMax)) * 4;
            var reverseMagnitude = Convert.ToUInt16(xboxData.RightTrigger.Map(0, 33000, _stopped, _reverseMax)) * 4;

            var outputVal = forwardMagnitude;

            if (reverseMagnitude > 6000)
            {
                outputVal = reverseMagnitude;
            }

            if (_warn)
            {
                outputVal = outputVal / 2;
            }

            await _maestroPwmController.SetChannelValue(outputVal, _movementChannel); //Channel 0 is the motor driver
        }

        private async Task<bool> StartVideoStreamAsync()
        {
            var mediaCapture = new MediaCapture();

            await mediaCapture.InitializeAsync();

            var encodingProperties = mediaCapture.VideoDeviceController
                .GetAvailableMediaStreamProperties(MediaStreamType.VideoPreview).ToList();

            var res = new List<string>();

            for (var i = 0; i < encodingProperties.Count; i++)
            {
                var p = (VideoEncodingProperties)encodingProperties[i];
                res.Add($"{i}, {p.Width}x{p.Height}, {p.FrameRate.Numerator / p.FrameRate.Denominator} fps, {p.Subtype}");
            }

            // set resolution
            await mediaCapture.VideoDeviceController.SetMediaStreamPropertiesAsync(MediaStreamType.VideoPreview, encodingProperties[Convert.ToInt32(6)]); //2, 8, 9 - 60fps = better

            var captureElement = new CaptureElement { Source = mediaCapture };

            await mediaCapture.StartPreviewAsync();

            var props = mediaCapture.VideoDeviceController.GetMediaStreamProperties(MediaStreamType.VideoPreview);

            await mediaCapture.SetEncodingPropertiesAsync(MediaStreamType.VideoPreview, props, null);

            while (!_remoteTokenSource.IsCancellationRequested && !_cancellationToken.IsCancellationRequested)
            {
                using (var stream = new InMemoryRandomAccessStream())
                {
                    try
                    {
                        await mediaCapture.CapturePhotoToStreamAsync(ImageEncodingProperties.CreateJpeg(), stream);
                    }
                    catch (Exception)
                    {
                        await Task.Delay(1000);
                        continue;
                    }

                    try
                    {
                        using (var reader = new DataReader(stream.GetInputStreamAt(0)))
                        {
                            var bytes = new byte[stream.Size];
                            await reader.LoadAsync((uint)stream.Size);
                            reader.ReadBytes(bytes);

                            if (_mqttClient == null)
                                continue;

                            try
                            {
                                await _mqttClient.PublishAsync(bytes, "autono-eye", TimeSpan.FromSeconds(1));
                            }
                            catch (TimeoutException)
                            {
                                //
                            }
                        }
                    }
                    catch (Exception)
                    {
                        //
                    }
                }
            }

            return false;
        }
    }
}