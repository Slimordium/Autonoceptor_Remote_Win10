using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.Core;
using Windows.UI.Core;
using Autonoceptor.Hardware;
using Autonoceptor.Hardware.Lcd;
using Autonoceptor.Shared;
using Autonoceptor.Shared.Utilities;
using Newtonsoft.Json;
using Nito.AsyncEx;
using NLog;

namespace Autonoceptor.Vehicle
{
    public class Car : Chassis
    {
        private readonly ILogger _logger = LogManager.GetCurrentClassLogger();

        private double _fpsTarget;

        protected const ushort _extraInputChannel = 14;

        private const ushort _lidarServoChannel = 17;

        private const int _rightLidarPwm = 1056;
        private const int _rightMidLidarPwm = 1282;
        private const int _centerLidarPwm = 1488;
        private const int _leftMidLidarPwm = 1682;
        private const int _leftLidarPwm = 1880;

        private const int _nerfDartChannel = 15;

        private int _safeDistance = 130;

        private Thread _lidarThread;

        private Thread _cruiseControlThread;

        private List<IDisposable> _sensorDisposables = new List<IDisposable>();

        private IDisposable _odoLcdDisposable;

        public WaypointQueue Waypoints { get; set; }

        protected string BrokerHostnameOrIp { get; set; }

        private readonly AsyncManualResetEvent _asyncResetEvent = new AsyncManualResetEvent(false);

        private CancellationTokenSource _cruiseControlCancellationTokenSource = new CancellationTokenSource();
        private IDisposable _lidarLcdDisposable;

        public CancellationTokenSource LidarCancellationTokenSource { get; set; } = new CancellationTokenSource();

        public int SafeDistance
        {
            get => Volatile.Read(ref _safeDistance);
            set
            {
                var val = value;

                if (val < 0)
                    val = 0;

                if (val > 300)
                    val = 300;

                Volatile.Write(ref _safeDistance, val);
            } 
        } 

        protected Car(CancellationTokenSource cancellationTokenSource, string brokerHostnameOrIp) 
            : base(cancellationTokenSource)
        {
            BrokerHostnameOrIp = brokerHostnameOrIp;

            cancellationTokenSource.Token.Register(async () =>
            {
                await Stop();
                await SetChannelValue(0, _lidarServoChannel);
                await SetChannelValue(0, MovementChannel);
                await SetChannelValue(0, SteeringChannel);
            });
        }

        protected void DisposeLcdWriters()
        {
            _odoLcdDisposable?.Dispose();
            _odoLcdDisposable = null;
        }

        protected async Task ConfigureLcdWriters()
        {
            _odoLcdDisposable = Odometer
                .GetObservable()
                .ObserveOnDispatcher()
                .Subscribe(
                    async odoData =>
                    {
                        await Lcd.Update(GroupName.Odometer, $"FPS: {Math.Round(odoData.FeetPerSecond, 1)}", $"Trv: {Math.Round(odoData.InTraveled / 12, 1)}ft");
                    });

            _lidarLcdDisposable = Lidar
                .GetObservable()
                .Sample(TimeSpan.FromMilliseconds(250))
                .ObserveOnDispatcher()
                .Subscribe(
                    async lidarData =>
                    {
                        if (!lidarData.IsValid)
                            return;

                        await Lcd.Update(GroupName.Lidar, $"L Dist: {lidarData.Distance}cm", $"L Str: {lidarData.Strength}");
                    });

            await Lcd.Update(GroupName.General, "Init Car", "Complete");
        }

        protected new async Task InitializeAsync()
        {
            await base.InitializeAsync();

            Waypoints = new WaypointQueue(.0000001, Lcd);

            await Stop();
            await DisableServos();

            await Lcd.Update(GroupName.LidarDangerZone, "Safe zone");
            await Lcd.SetUpCallback(GroupName.LidarDangerZone, IncrementSafeDistance);
            await Lcd.SetDownCallback(GroupName.LidarDangerZone, DecrementSafeDistance);

            await Lcd.Update(GroupName.GpsNavSpeed, "Nav feet p/sec", $" {_fpsTarget} fps");
            await Lcd.SetUpCallback(GroupName.GpsNavSpeed, IncrementSpeed);
            await Lcd.SetDownCallback(GroupName.GpsNavSpeed, DecrementSpeed);
        }

        private string IncrementSafeDistance()
        {
            var sd = SafeDistance;

            sd++;

            if (sd > 300)
                sd = 300;

            SafeDistance = sd;

            return $" {sd}cm";
        }

        private string DecrementSafeDistance()
        {
            var sd = SafeDistance;

            sd--;

            if (sd < 40)
                sd = 40;

            SafeDistance = sd;

            return $" {sd}cm";
        }

        private string IncrementSpeed()
        {
            var fpsTarget = _fpsTarget;

            fpsTarget = fpsTarget + .25;

            if (fpsTarget > 4)
                fpsTarget = 4;

            _fpsTarget = fpsTarget;

            return $" {fpsTarget} fps";
        }

        private string DecrementSpeed()
        {
            var fpsTarget = _fpsTarget;

            fpsTarget = fpsTarget - .25;

            if (fpsTarget < 0)
                fpsTarget = 0;

            _fpsTarget = fpsTarget;

            return $" {fpsTarget} fps";
        }

        public void StartLidarTask()
        {
            if (LidarCancellationTokenSource == null || !LidarCancellationTokenSource.IsCancellationRequested)
            {
                LidarCancellationTokenSource?.Cancel();
                LidarCancellationTokenSource?.Dispose();
                LidarCancellationTokenSource = new CancellationTokenSource();
            }

            _lidarThread = new Thread(async () =>
            {
                await CoreApplication.MainView.CoreWindow.Dispatcher.RunAsync(CoreDispatcherPriority.Normal,
                    async () =>
                    {
                        var ir = await SetChannelValue(_rightMidLidarPwm * 4, _lidarServoChannel);

                        await Task.Delay(250);

                        while (!LidarCancellationTokenSource.IsCancellationRequested)
                        {
                            var safeDistance = SafeDistance;

                            try
                            {
                                if (Stopped)
                                {
                                    await Task.Delay(500);
                                    continue;
                                }

                                var data = new List<LidarData>();

                                for (var pwm = _rightMidLidarPwm; pwm < _leftMidLidarPwm; pwm += 10)
                                {
                                    var r = await SetChannelValue(pwm * 4, _lidarServoChannel);

                                    var lidarData = await Lidar.GetLatest();

                                    if (!lidarData.IsValid)
                                        continue;

                                    lidarData.Angle = Math.Round(pwm.Map(_rightMidLidarPwm, _leftMidLidarPwm, 35, -35));
                                    data.Add(lidarData);

                                    if (lidarData.Distance < safeDistance)
                                    {
                                        if (lidarData.Angle < 0)
                                        {
                                            var newAngle = lidarData.Angle.Map(-35, 0, 0, -35);

                                            await SetVehicleHeadingLidar(SteeringDirection.Right,
                                                Math.Abs(newAngle * 8));
                                        }
                                        else
                                        {
                                            var newAngle = lidarData.Angle.Map(35, 0, 0, 35);
                                            await SetVehicleHeadingLidar(SteeringDirection.Left,
                                                Math.Abs(newAngle * 8));
                                        }
                                    }
                                }

                                var dangerAngles = data.Where(d => d.Distance < safeDistance).ToList();

                                data = new List<LidarData>();

                                for (var pwm = _leftMidLidarPwm; pwm >= _rightMidLidarPwm; pwm -= 10)
                                {
                                    var r = await SetChannelValue(pwm * 4, _lidarServoChannel);

                                    var lidarData = await Lidar.GetLatest();

                                    if (!lidarData.IsValid)
                                        continue;

                                    lidarData.Angle = Math.Round(pwm.Map(_leftMidLidarPwm, _rightMidLidarPwm, -35, 35));
                                    data.Add(lidarData);

                                    if (lidarData.Distance < safeDistance)
                                    {
                                        if (lidarData.Angle < 0)
                                        {
                                            var newAngle = lidarData.Angle.Map(-35, 0, 0, -35);
                                            await SetVehicleHeadingLidar(SteeringDirection.Right,
                                                Math.Abs(newAngle * 8));
                                        }
                                        else
                                        {
                                            var newAngle = lidarData.Angle.Map(35, 0, 0, 35);
                                            await SetVehicleHeadingLidar(SteeringDirection.Left,
                                                Math.Abs(newAngle * 8));
                                        }
                                    }
                                }

                                dangerAngles.AddRange(data.Where(d => d.Distance < safeDistance).ToList());

                                if (dangerAngles.Any())
                                {
                                    _asyncResetEvent.Set();
                                }
                                else
                                {
                                    _asyncResetEvent.Reset();
                                }
                            }
                            catch (Exception e)
                            {
                                _logger.Log(LogLevel.Error, $"Sweep: {e.Message}");
                            }
                        }
                    });
            }) {IsBackground = true};
            _lidarThread.Start();
        }

        protected void ConfigureSensorPublish()
        {
            if (MqttClient == null)
                return;

            if (_sensorDisposables.Any())
            {
                _sensorDisposables.ForEach(disposable =>
                {
                    disposable.Dispose();
                });
            }

            _sensorDisposables = new List<IDisposable>();

            _sensorDisposables.Add(
                Gps.GetObservable()
                    .ObserveOnDispatcher()
                    .Subscribe(async gpsFixData =>
                    {
                        try
                        {
                            await MqttClient.PublishAsync(JsonConvert.SerializeObject(gpsFixData), "autono-gps");
                        }
                        catch (Exception)
                        {
                            //Yum
                        }
                    }));

            _sensorDisposables.Add(
                Lidar.GetObservable()
                    .ObserveOnDispatcher()
                    .Subscribe(async lidarData =>
                    {
                        try
                        {
                            await MqttClient.PublishAsync(JsonConvert.SerializeObject(lidarData), "autono-lidar");
                        }
                        catch (Exception)
                        {
                            //Yum
                        }
                    }));
        }

        private void StartCruiseControlThread()
        {
            _cruiseControlThread = new Thread(async () =>
            {
                await CoreApplication.MainView.CoreWindow.Dispatcher.RunAsync(CoreDispatcherPriority.Normal,
                    async () =>
                    {
                        var moveMagnitude = 0d;
                        var starting = true;
                        var isStuck = false;

                        while (!_cruiseControlCancellationTokenSource.IsCancellationRequested)
                        {
                            var fpsTarget = _fpsTarget;

                            var odometerList = new List<OdometerData>
                            {
                                await Odometer.GetLatest(),
                                await Odometer.GetLatest()
                            };

                            var currentFps = odometerList.Average(d => d.FeetPerSecond);

                            if (currentFps < 1 && !starting)
                            {
                                isStuck = true;
                            }

                            //Give it some wiggle room
                            if (currentFps < fpsTarget + .5 && currentFps > fpsTarget - .5)
                            {
                                return;
                            }

                            if (currentFps < fpsTarget)
                                moveMagnitude = moveMagnitude + 10;

                            if (currentFps > fpsTarget)
                            {
                                starting = false;

                                if (moveMagnitude > 50)
                                {
                                    moveMagnitude = moveMagnitude - 1.5;
                                }
                                else if (moveMagnitude > 40)
                                {
                                    moveMagnitude = moveMagnitude - 1;
                                }
                                else
                                {
                                    moveMagnitude = moveMagnitude - .5;
                                }
                            }

                            if (moveMagnitude > 55)
                                moveMagnitude = 55;

                            if (moveMagnitude < 0)
                                moveMagnitude = 0;

                            if (Stopped)
                                return;

                            if (!isStuck)
                            {
                                await SetVehicleTorque(MovementDirection.Forward, moveMagnitude);
                            }
                            else
                            {
                                // shoot a nerf dart
                                //await SetChannelValue(1500 * 4, 15);

                                // turn wheels slightly to the left
                                await SetVehicleHeading(SteeringDirection.Left, 70);

                                // reverse
                                await SetVehicleTorque(MovementDirection.Reverse, 60);
                                await Task.Delay(1800);

                                // now continue trying to get to next waypoint
                                await SetVehicleTorque(MovementDirection.Forward, 60);

                                isStuck = false;
                            }
                        }
                    });
            }) {IsBackground = true};
            _cruiseControlThread.Start();
        }

        protected async Task SetVehicleTorque(MovementDirection direction, double magnitude)
        {
            var moveValue = StoppedPwm * 4;

            if (magnitude > 100)
                magnitude = 100;

            switch (direction)
            {
                case MovementDirection.Forward:
                    moveValue = Convert.ToUInt16(magnitude.Map(0, 100, StoppedPwm, ForwardPwmMax)) * 4;
                    break;
                case MovementDirection.Reverse:
                    moveValue = Convert.ToUInt16(magnitude.Map(0, 100, StoppedPwm, ReversePwmMax)) * 4;
                    break;
            }

            await SetChannelValue(moveValue, MovementChannel);
        }

        /// <summary>
        /// Sets target feet per second (FPS)
        /// </summary>
        /// <param name="feetPerSecond"></param>
        /// <returns></returns>
        public async Task SetCruiseControlFps(double feetPerSecond)
        {
            _cruiseControlCancellationTokenSource?.Cancel();

            await Task.Delay(250);

            _cruiseControlCancellationTokenSource?.Dispose();

            _cruiseControlCancellationTokenSource = new CancellationTokenSource();

            _fpsTarget = feetPerSecond;

            await SetVehicleTorque(MovementDirection.Forward, 40);

            StartCruiseControlThread();
        }

        public async Task StopCruiseControl()
        {
            _cruiseControlCancellationTokenSource?.Cancel();
            _cruiseControlCancellationTokenSource?.Dispose();
            _cruiseControlCancellationTokenSource = new CancellationTokenSource();

            await Stop();
        }

        /// <summary>
        /// How many feet per second 
        /// </summary>
        /// <param name="feetPerSecond"></param>
        public void UpdateCruiseControl(double feetPerSecond)
        {
            if (feetPerSecond > 4)
                feetPerSecond = 4;

            if (feetPerSecond < 1.5)
                feetPerSecond = 1.5;

            _fpsTarget = feetPerSecond;
        }

        public async Task Stop(bool isCanceled = false)
        {
            if (isCanceled)
            {
                Stopped = false;

                await Lcd.Update(GroupName.Car, "Started", string.Empty, true);

                return;
            }

            await SetChannelValue(StoppedPwm * 4, MovementChannel);
            await SetChannelValue(0, SteeringChannel);

            if (Stopped)
                return;

            Stopped = true;

            await Lcd.Update(GroupName.Car, "Stopped", "", true);
        }

        public async Task DisableServos()
        {
            await SetChannelValue(0, MovementChannel);
            await SetChannelValue(0, SteeringChannel);
        }

        private async Task SetVehicleHeadingLidar(SteeringDirection direction, double magnitude)
        {
            try
            {
                var steerValue = CenterPwm * 4;

                if (magnitude > 100)
                    magnitude = 100;

                switch (direction)
                {
                    case SteeringDirection.Left:
                        steerValue = Convert.ToUInt16(magnitude.Map(0, 100, CenterPwm, LeftPwmMax)) * 4;
                        break;
                    case SteeringDirection.Right:
                        steerValue = Convert.ToUInt16(magnitude.Map(0, 100, CenterPwm, RightPwmMax)) * 4;
                        break;
                }

                await SetChannelValue(steerValue, SteeringChannel);
            }
            catch (Exception err)
            {
                _logger.Log(LogLevel.Error, $"Error in SetVehicleHeadingLidar: {err}");
            }
        }

        protected async Task SetVehicleHeading(SteeringDirection direction, double magnitude)
        {
            try
            {
                if (Stopped)
                {
                    await SetChannelValue(StoppedPwm * 4, MovementChannel);
                    await SetChannelValue(0, SteeringChannel);
                    return;
                }

                if (!_asyncResetEvent.IsSet) //Will this work? We shall see
                    return;

                var steerValue = CenterPwm * 4;

                if (magnitude > 100)
                    magnitude = 100;

                switch (direction)
                {
                    case SteeringDirection.Left:
                        steerValue = Convert.ToUInt16(magnitude.Map(0, 100, CenterPwm, LeftPwmMax)) * 4;
                        break;
                    case SteeringDirection.Right:
                        steerValue = Convert.ToUInt16(magnitude.Map(0, 100, CenterPwm, RightPwmMax)) * 4;
                        break;
                }

                await SetChannelValue(steerValue, SteeringChannel);
            }
            catch (Exception err)
            {
                _logger.Log(LogLevel.Error, $"Error in SetVehicleHeading: {err}");
            }
        }
    }
}