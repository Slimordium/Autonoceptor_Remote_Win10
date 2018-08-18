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

        private ushort _navEnableChannel = 8;
        private ushort _recordWaypointsChannel = 10;
        private ushort _enableRemoteChannel = 9;

        private int _lidarRange;
        private bool _warn;
        private bool _emergencyStopped;

        private MqttClient _mqttClient;

        private readonly List<IDisposable> _disposables = new List<IDisposable>();

        private bool _recordWaypoints;

        private StorageFolder _waypointFolder = ApplicationData.Current.LocalFolder;

        private List<IDisposable> _sensorDisposables = new List<IDisposable>();

        private readonly Tf02Lidar _lidar = new Tf02Lidar();
        private readonly SparkFunSerial16X2Lcd _lcd = new SparkFunSerial16X2Lcd();

        private StorageFile _waypointFile;// = await storageFolder.CreateFileAsync($"waypoints-{DateTime.Now:MMM-dd-HH:mm:ss.ff}.txt", CreationCollisionOption.OpenIfExists);

        public async Task InitializeAsync(CancellationTokenSource cancellationTokenSource)
        {
            await Gps.InitializeAsync();

            _cancellationToken = cancellationTokenSource.Token;

            await _maestroPwmController.InitializeAsync(_cancellationToken);

            cancellationTokenSource.Token.Register(async () => { await Stop(); });

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
                async isSet =>
                {
                    if (isSet)
                    {
                        _recordWaypoints = true;

                        if (_waypointFile == null)
                        {
                            _waypointFile = await _waypointFolder.CreateFileAsync($"waypoints-{DateTime.Now:MMM-dd-HH:mm:ss.ff}.txt", CreationCollisionOption.OpenIfExists);
                        }
                    }

                    if (isSet)
                        return;

                    _recordWaypoints = false;
                    _waypointFile = null;
                }));

            //Write GPS fix data to file, if switch is closed
            _disposables.Add(Gps.GetObservable(_cancellationToken)
                .ObserveOnDispatcher()
                .Subscribe(async gpsFixData =>
                {
                    if (_waypointFile == null || !_recordWaypoints)
                        return;

                    try
                    {
                        await FileIO.WriteTextAsync(_waypointFile, gpsFixData.ToString());
                    }
                    catch (Exception)
                    {
                        //
                    }
                }));

        }

        private string _brokerIp;

        private IDisposable _remoteDisposable;

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

                    await _mqttClient.PublishAsync(JsonConvert.SerializeObject(fix), "autono-gps");
                }));

            _sensorDisposables.Add(_lidar.GetObservable(_cancellationToken).ObserveOnDispatcher()
                .Where(lidarData => lidarData != null).Sample(TimeSpan.FromMilliseconds(150)).Subscribe(
                    async lidarData =>
                    {
                        if (_mqttClient == null)
                            return;

                        await _mqttClient.PublishAsync(JsonConvert.SerializeObject(lidarData), "autono-lidar");
                    }));
        }

        public async Task UpdateLidarRange(int range)
        {
            _lidarRange = range;

            if (range <= 30)
            {
                await EmergencyStop();
            }

            if (range > 30 && range <= 60)
            {
                await EmergencyStop(true);

                _warn = true;
            }

            if (range > 60)
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

            var direction = 5932;

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