using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Devices.HumanInterfaceDevice;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Storage.Streams;
using Windows.UI.Xaml.Controls;
using Autonoceptor.Host.Utility.GpsNav;
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
        private readonly Tf02Lidar _lidar = new Tf02Lidar();
        private readonly SparkFunSerial16X2Lcd _lcd = new SparkFunSerial16X2Lcd();
        private MaestroPwmController _maestroPwm;
        private readonly Gps _gps = new Gps();
        private readonly XboxDevice _xboxDevice = new XboxDevice();

        private GpsFixData _gpsFixData = new GpsFixData();

        private CancellationToken _cancellationToken;
        private CancellationTokenSource _remoteTokenSource = new CancellationTokenSource();

        private readonly SemaphoreSlim _initMqttSemaphore = new SemaphoreSlim(1,1);

        private int _rightMax = 1861;
        private int _center = 1321;
        private int _leftMax = 837;

        private int _reverseMax = 1072;
        private int _stopped = 1471;
        private int _forwardMax = 1856; //1856

        private ushort _movementChannel = 0;
        private ushort _steeringChannel = 1;

        private ushort _navEnableChannel = 12;
        private ushort _recordWaypointsChannel = 13;
        private ushort _enableRemoteChannel = 14;

        private bool _recordWaypoints;

        private readonly string _brokerIp;

        private int _lidarRange;
        private bool _warn;
        private bool _emergencyStopped;

        private MqttClient _mqttClient;

        private readonly List<IDisposable> _disposables = new List<IDisposable>();
        private IDisposable _remoteDisposable;
        private IDisposable _waypointNavigationDisposable;
        private List<IDisposable> _sensorDisposables = new List<IDisposable>();

        private WaypointList _waypointList = new WaypointList();

        private bool _videoRunning;

        private bool _xboxMissing;

        private int _waypointIndex;

        private async Task CheckXboxConnection()
        {
            var devices = await DeviceInformation.FindAllAsync(HidDevice.GetDeviceSelector(0x01, 0x05));

            if (devices.Any())
            {
                return;
            }

            if (Volatile.Read(ref _xboxMissing))
                return;

            Volatile.Write(ref _xboxMissing, true);

            await EmergencyStop();

            await Stop();
        }

        public async Task InitializeAsync(CancellationTokenSource cancellationTokenSource)
        {
            _cancellationToken = cancellationTokenSource.Token;

            _maestroPwm = new MaestroPwmController(new ushort[]{12, 13, 14 });

            await _maestroPwm.InitializeAsync(_cancellationToken);

            await _lcd.InitializeAsync();

            await Task.Delay(1000);

            await _lcd.WriteAsync("Initializing...");

            var initXbox = await _xboxDevice.InitializeAsync(_cancellationToken);

            if (initXbox)
            {
                await _lcd.WriteAsync("Found Xbox controller", 2);

                _disposables.Add(_xboxDevice.GetObservable()
                    .Where(xboxData => xboxData != null)
                    .ObserveOnDispatcher()
                    .Subscribe(async xboxData =>
                    {
                        await OnNextXboxData(xboxData); 
                    }));

                //If the controller connection was lost, stop the car...
                _disposables.Add(Observable.Interval(TimeSpan.FromMilliseconds(250))
                    .ObserveOnDispatcher()
                    .Subscribe(async _ => { await CheckXboxConnection(); }));
            }

            await _gps.InitializeAsync();
            await _lidar.InitializeAsync();

            cancellationTokenSource.Token.Register(async () =>
            {
                await DisableServos();

                await _lcd.WriteAsync("Disposed :]", 1);
                await _lcd.WriteAsync("................", 2);
            });

            //TODO: Check GPS fix type is "estimated" which means that DR is working, otherwise it is not! 
            //TODO: Check odometer scaling factor in NS-DR module. Just set the NS-DR to pedestrian mode
            //Write GPS fix data to file, if switch is closed. _gps publishes fix data once a second
            _disposables.Add(_gps.GetObservable(_cancellationToken)
                .Where(wp => wp.Lat != 0 && wp.Lon != 0)
                .ObserveOnDispatcher()
                .Subscribe(async gpsFixData =>
                {
                    //await _lcd.WriteAsync($"{gpsFixData.Lat}", 1);
                    //await _lcd.WriteAsync($"{gpsFixData.Lon}", 2);

                    if (!_recordWaypoints)
                        return;

                    if (_waypointList.Any(data => data.Lat == gpsFixData.Lat && data.Lon == gpsFixData.Lon))
                    {
                        return; //Dont add multiples of the same point
                    }

                    _waypointList.Add(gpsFixData);
                }));

            _disposables.Add(
                _lidar.LidarObservable
                .ObserveOnDispatcher()
                .Subscribe(async rangeData =>
                {
                    await UpdateLidarRange(rangeData.Distance); 
                }));

            //_disposables.Add(_lidar.GetObservable(_cancellationToken)
            //    .Sample(TimeSpan.FromMilliseconds(100))
            //    .ObserveOnDispatcher()
            //    .Subscribe(async rangeData =>
            //    {
            //        await _lcd.WriteAsync($"LIDAR: {_lidarRange}cm", 2);
            //    }));

            await _lcd.WriteAsync("Initialized", 1);

            if (_maestroPwm == null)
                return;

            //_navEnableChannel
            _disposables.Add(_maestroPwm.GetObservable()
                .Where(channel => channel.ChannelId == _navEnableChannel)
                .ObserveOnDispatcher()
                .Subscribe(
                async channel =>
                {
                    if (channel.DigitalValue)
                    {
                        _waypointList = await _waypointList.Load();

                        if (_waypointList == null || !_waypointList.Any())
                        {
                            await _lcd.WriteAsync("No waypoints found", 1);
                            return;
                        }

                        await _lcd.WriteAsync("Begining Waypoint follow", 1);

                        _waypointNavigationDisposable = _gps.GetObservable(_cancellationToken).ObserveOnDispatcher().Subscribe(async gpsFixData =>
                        {
                            if (_waypointIndex >= _waypointList.Count)
                            {
                                await _lcd.WriteAsync($"Nav complete {_waypointIndex}", 1);
                                return;
                            }

                            var moveReq = GpsNavUtility.CalculateMoveRequest(_waypointList[_waypointIndex], gpsFixData, _cancellationToken);

                            //Override for now, 15 = 10%
                            moveReq.MovementMagnitude = 15;

                            await MoveRequest(moveReq);

                            var distance = GpsExtensions.GetDistanceAndHeadingToDestination(gpsFixData.Lat, gpsFixData.Lon, _waypointList[_waypointIndex].Lat, _waypointList[_waypointIndex].Lon)[0];

                            await _lcd.WriteAsync($"Direction {moveReq.SteeringDirection}", 1);
                            await _lcd.WriteAsync($"Distance {distance}", 2);

                            if (distance <= 28)
                            {
                                _waypointIndex++;
                            }

                            if (_waypointIndex >= _waypointList.Count)
                            {
                                await EmergencyStop();
                            }
                        });
                    }
                    else
                    {
                        await _lcd.WriteAsync("Ending Waypoint follow", 1);

                        _waypointNavigationDisposable?.Dispose();
                        _waypointNavigationDisposable = null;

                        _waypointIndex = 0;
                    }
                }));

            //_enableRemoteChannel
            //If the enable remote switch is closed, start streaming video/sensor data
            _disposables.Add(_maestroPwm.GetObservable()
                .Where(channel => channel.ChannelId == _enableRemoteChannel)
                .ObserveOnDispatcher()
                .Subscribe(
                async channel =>
                {
                    if (channel.DigitalValue)
                    {
                        await _lcd.WriteAsync("Start pub...", 2);

                        _remoteTokenSource = new CancellationTokenSource();

                        await PublishSensorData();

                        await ConnectToRemote();

                        //StartVideoStreamAsync();
                    }
                    else
                    {
                        _remoteTokenSource?.Cancel();

                        _remoteDisposable?.Dispose();

                        foreach (var sensor in _sensorDisposables)
                        {
                            sensor.Dispose();
                        }

                        _sensorDisposables = new List<IDisposable>();

                        _mqttClient?.Dispose();
                        _mqttClient = null;
                    }
                    
                }));

            //Set _recordWaypoints to "true" if the channel is pulled high
            _disposables.Add(_maestroPwm.GetObservable()
                .Where(channel => channel.ChannelId == _recordWaypointsChannel)
                .ObserveOnDispatcher()
                .Subscribe(
                async channel =>
                {
                    if (channel.DigitalValue)
                    {
                        await _lcd.WriteAsync("Start Waypoint Tracking", 2);

                        _recordWaypoints = true;

                        _waypointList = new WaypointList();
                    }
                    else
                    {
                        await _waypointList.Save();

                        _recordWaypoints = false;
                    }
                }));
        }

        public async Task InitializePwm()
        {
            await _maestroPwm.InitializeAsync(_cancellationToken);

            await _maestroPwm.SetChannelValue(0, _steeringChannel); //Turn off PWM
            await _maestroPwm.SetChannelValue(0, _movementChannel); //Turn off PWM
        }

        public Conductor(string brokerIp)
        {
            _brokerIp = brokerIp;
        }

        private async Task<bool> InitMqtt()
        {
            await _initMqttSemaphore.WaitAsync(_cancellationToken);

            if (_mqttClient == null)
            {
                _mqttClient?.Dispose();
                _mqttClient = null;

                _mqttClient = new MqttClient("autonoceptor-control", _brokerIp, 1883);
                var status = await _mqttClient.InitializeAsync();

                if (status != Status.Initialized)
                {
                    await _lcd.WriteAsync("MQTT Failed", 2);

                    _mqttClient.Dispose();
                    _mqttClient = null;

                    return false;
                }

                await _lcd.WriteAsync("MQTT Connected", 2);
            }

            _initMqttSemaphore.Release(1);

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
        }

        private async Task PublishSensorData()
        {
            if (!await InitMqtt())
                return;

            foreach (var sensorDisposable in _sensorDisposables)
            {
                sensorDisposable.Dispose();
            }

            _sensorDisposables = new List<IDisposable>();

            _sensorDisposables.Add(_gps.GetObservable(_cancellationToken)
                .Where(fix => fix != null)
                .ObserveOnDispatcher()
                .Subscribe(async fix =>
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

            _sensorDisposables.Add(_lidar.GetObservable(_cancellationToken)
                .Where(lidarData => lidarData != null)
                .Sample(TimeSpan.FromMilliseconds(100))
                .ObserveOnDispatcher()
                .Subscribe(async lidarData =>
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

        private async Task UpdateLidarRange(int range)
        {
            if (range < 40)
                range = 40;

            Volatile.Write(ref _lidarRange, range);

            if (range <= 70)
            {
                await EmergencyStop();
            }

            if (range > 70 && range <= 130)
            {
                //await EmergencyStop(true);

                Volatile.Write(ref _warn, true);
            }

            if (range > 130)
            {
                //await EmergencyStop(true);

                Volatile.Write(ref _warn, false);
            }
        }

        private async Task Stop()
        {
            await _maestroPwm.SetChannelValue(_stopped - 50 * 4, _movementChannel); //Momentary reverse ... helps stop quickly

            await Task.Delay(50);

            await _maestroPwm.SetChannelValue(_stopped * 4, _movementChannel);

            await DisableServos();
        }

        private async Task DisableServos()
        {
            await _maestroPwm.SetChannelValue(0, _movementChannel);
            await _maestroPwm.SetChannelValue(0, _steeringChannel);

            await Task.Delay(100);
        }

        public async Task EmergencyStop(bool isCanceled = false)
        {
            if (isCanceled)
            {
                if (!Volatile.Read(ref _emergencyStopped))
                    return;

                Volatile.Write(ref _emergencyStopped, false);

                await _lcd.WriteAsync("E-Stop canceled", 2);

                return;
            }

            if (Volatile.Read(ref _emergencyStopped))
            {
                await _lcd.WriteAsync($"E-Stop @ {_lidarRange}cm", 2);
                return;
            }

            Volatile.Write(ref _emergencyStopped, true);

            await Stop();
        }


        /// <summary>
        /// The magnitudes are in % 0 - 100
        /// </summary>
        /// <param name="request"></param>
        /// <returns></returns>
        public async Task MoveRequest(MoveRequest request)
        {
            if (_cancellationToken.IsCancellationRequested || Volatile.Read(ref _emergencyStopped))
            {
                await EmergencyStop();
                return;
            }

            var moveValue = _stopped * 4;
            var steerValue = _center * 4;

            switch (request.SteeringDirection)
            {
                case SteeringDirection.Left:
                    steerValue = Convert.ToUInt16(request.SteeringMagnitude.Map(0, 100, _center, _leftMax)) * 4;
                    break;
                case SteeringDirection.Right:
                    steerValue = Convert.ToUInt16(request.SteeringMagnitude.Map(0, 100, _center, _rightMax)) * 4;
                    break;
            }

            await _maestroPwm.SetChannelValue(steerValue, _steeringChannel);

            switch (request.MovementDirection)
            {
                case MovementDirection.Forward:
                    moveValue = Convert.ToUInt16(request.MovementMagnitude.Map(0, 100, _stopped, _forwardMax)) * 4;
                    break;
                case MovementDirection.Reverse:
                    moveValue = Convert.ToUInt16(request.MovementMagnitude.Map(0, 100, _stopped, _reverseMax)) * 4;
                    break;
            }

            await _maestroPwm.SetChannelValue(moveValue, _movementChannel);
        }

        private async Task OnNextXboxData(XboxData xboxData)
        {
            if (xboxData.FunctionButtons.Contains(FunctionButton.A))
            {
                await EmergencyStop(true);
            }

            if (_cancellationToken.IsCancellationRequested || xboxData.FunctionButtons.Contains(FunctionButton.X))
            {
                await EmergencyStop();
                return;
            }

            if (Volatile.Read(ref _emergencyStopped))
                return;

            var direction = _center * 4;

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

            await _maestroPwm.SetChannelValue(direction, _steeringChannel); //ChannelId 1 is Steering

            var reverseMagnitude = Convert.ToUInt16(xboxData.LeftTrigger.Map(0, 33000, _stopped, _reverseMax)) * 4;
            var forwardMagnitude = Convert.ToUInt16(xboxData.RightTrigger.Map(0, 33000, _stopped, _forwardMax)) * 4;

            var outputVal = forwardMagnitude;

            if (reverseMagnitude < 5500 || Volatile.Read(ref _emergencyStopped))
            {
                outputVal = reverseMagnitude;
            }

            await _maestroPwm.SetChannelValue(outputVal, _movementChannel); //ChannelId 0 is the motor driver
        }

        private async Task<bool> StartVideoStreamAsync()
        {
            if (Volatile.Read(ref _videoRunning))
                return true;

            Volatile.Write(ref _videoRunning, true);

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
            await mediaCapture.VideoDeviceController.SetMediaStreamPropertiesAsync(MediaStreamType.VideoPreview, encodingProperties[Convert.ToInt32(8)]); //2, 8, 9 - 60fps = better

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

            Volatile.Write(ref _videoRunning, false);

            return false;
        }
    }
}