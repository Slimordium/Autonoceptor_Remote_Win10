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

        protected const int _rightLidarPwm = 1056;
        private const int _rightMidLidarPwm = 1282;
        protected const int _centerLidarPwm = 1488;
        private const int _leftMidLidarPwm = 1682;
        protected const int _leftLidarPwm = 1880;

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
                await SetChannelValue(0, LidarServoChannel);
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

            StartLidarThread();
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

        public void StopLidarThread()
        {
            if (LidarCancellationTokenSource == null || !LidarCancellationTokenSource.IsCancellationRequested)
            {
                LidarCancellationTokenSource?.Cancel();
                LidarCancellationTokenSource?.Dispose();
                LidarCancellationTokenSource = new CancellationTokenSource();
            }
        }

        public void StartLidarThread()
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
                        var ir = await SetChannelValue(_centerLidarPwm * 4, LidarServoChannel);

                        await Task.Delay(250);

                        var originalTargetFps = _fpsTarget;

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

                                var lidarData = await Lidar.GetLatest();

                                if (lidarData.Distance < safeDistance)
                                {
                                    //Danger
                                    _asyncResetEvent.Set();
                                    
                                    await UpdateCruiseControl(1.5);

                                    await Lcd.Update(GroupName.LidarDangerZone, $"Danger @ {lidarData.Distance}cm", string.Empty, true);

                                    var lidarCv = await GetChannelValue(LidarServoChannel) / 4;

                                    var steerMagnitude = 0d;

                                    var steerDirection = SteeringDirection.Center;

                                    if (lidarCv > _centerLidarPwm)
                                    {
                                        steerDirection = SteeringDirection.Right;
                                        steerMagnitude = lidarCv.Map(_centerLidarPwm, _leftLidarPwm, 100, 0);
                                    }
                                    else if (lidarCv < _centerLidarPwm)
                                    {
                                        steerDirection = SteeringDirection.Left;
                                        steerMagnitude = lidarCv.Map(_rightLidarPwm, _centerLidarPwm, 100, 0);
                                    }

                                    _logger.Log(LogLevel.Info, $"LidarPwm: {lidarCv}, SteerDirection: {steerDirection}, SteerMagnitude: {steerMagnitude} ");

                                    await SetVehicleHeading(steerDirection, steerMagnitude, true);

                                    await Task.Delay(250);
                                }
                                else
                                {
                                    //Safe
                                    _asyncResetEvent.Reset();

                                    await UpdateCruiseControl(originalTargetFps);

                                    await Lcd.Update(GroupName.LidarDangerZone, $"Safe @ {lidarData.Distance}cm", string.Empty);

                                    originalTargetFps = _fpsTarget;
                                }

                            }
                            catch (Exception e)
                            {
                                _logger.Log(LogLevel.Error, $"Lidar: {e.Message}");
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
                            await MqttClient.PublishAsync(JsonConvert.SerializeObject(gpsFixData), "autono-gps").ConfigureAwait(false);
                        }
                        catch (Exception)
                        {
                            //Yum
                        }
                    }));

            //_sensorDisposables.Add(
            //    Lidar.GetObservable()
            //        .ObserveOnDispatcher()
            //        .Subscribe(async lidarData =>
            //        {
            //            try
            //            {
            //                await MqttClient.PublishAsync(JsonConvert.SerializeObject(lidarData), "autono-lidar");
            //            }
            //            catch (Exception)
            //            {
            //                //Yum
            //            }
            //        }));
        }

        private void StartCruiseControlThread()
        {
            _cruiseControlThread = new Thread(async () =>
            {
                await CoreApplication.MainView.CoreWindow.Dispatcher.RunAsync(CoreDispatcherPriority.Normal,
                    async () =>
                    {
                        var moveMagnitude = 30d;
                        var starting = true;
                        var isStuck = false;

                        await Lcd.Update(GroupName.Odometer, $"Cruise started", $" {_fpsTarget} fps");

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
                                continue;
                            }

                            if (currentFps < fpsTarget)
                                moveMagnitude = moveMagnitude + 5;

                            if (currentFps > fpsTarget)
                            {
                                starting = false;

                                if (moveMagnitude > 40)
                                {
                                    moveMagnitude = moveMagnitude - 1.5;
                                }
                                else if (moveMagnitude > 30)
                                {
                                    moveMagnitude = moveMagnitude - 1;
                                }
                                else
                                {
                                    moveMagnitude = moveMagnitude - .5;
                                }
                            }

                            if (moveMagnitude > 65)
                                moveMagnitude = 65;

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
                                await Lcd.Update(GroupName.Odometer, $"Stuck!", string.Empty);

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

            var scv = await SetChannelValue(moveValue, MovementChannel);
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

            await SetVehicleTorque(MovementDirection.Forward, 30);

            StartCruiseControlThread();
        }

        public async Task StopCruiseControl()
        {
            _cruiseControlCancellationTokenSource?.Cancel();
            _cruiseControlCancellationTokenSource?.Dispose();
            _cruiseControlCancellationTokenSource = new CancellationTokenSource();

            _fpsTarget = 0;

            await Lcd.Update(GroupName.Odometer, $"Cruise stopped", string.Empty);

            await Stop();
        }

        /// <summary>
        /// How many feet per second 
        /// </summary>
        /// <param name="feetPerSecond"></param>
        public async Task UpdateCruiseControl(double feetPerSecond)
        {
            if (feetPerSecond > 5)
                feetPerSecond = 5;

            if (feetPerSecond < 0)
                feetPerSecond = 0;

            _fpsTarget = feetPerSecond;

            await Lcd.Update(GroupName.Odometer, $"Cruise updated", $" {feetPerSecond} fps");
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

        protected async Task SetVehicleHeading(SteeringDirection direction, double magnitude, bool isSourceLidar = false)
        {
            try
            {
                if (Stopped)
                {
                    var mcv = await SetChannelValue(StoppedPwm * 4, MovementChannel);
                    var scv = await SetChannelValue(0, SteeringChannel);
                    var lcv = await SetChannelValue(0, LidarServoChannel);

                    _asyncResetEvent.Set();
                    return;
                }

                if (!_asyncResetEvent.IsSet && !isSourceLidar) //Will this work? We shall see
                {
                    return;
                }

                var steerValue = CenterPwm * 4;
                var lidarAngle = _centerLidarPwm * 4;

                if (magnitude > 100)
                    magnitude = 100;

                if (magnitude < 0)
                    magnitude = 0;

                switch (direction)
                {
                    case SteeringDirection.Left:
                        steerValue = Convert.ToUInt16(magnitude.Map(0, 100, CenterPwm, LeftPwmMax)) * 4;
                        lidarAngle = Convert.ToUInt16(magnitude.Map(0, 100, _centerLidarPwm, _leftLidarPwm)) * 4;
                        break;
                    case SteeringDirection.Right:
                        steerValue = Convert.ToUInt16(magnitude.Map(0, 100, CenterPwm, RightPwmMax)) * 4;
                        lidarAngle = Convert.ToUInt16(magnitude.Map(0, 100, _centerLidarPwm, _rightLidarPwm)) * 4;
                        break;
                }

                if (!isSourceLidar)
                {
                    var rsv = await SetChannelValue(lidarAngle, LidarServoChannel);
                }

                var cv = await SetChannelValue(steerValue, SteeringChannel);
            }
            catch (Exception err)
            {
                _logger.Log(LogLevel.Error, $"Error in SetVehicleHeading: {err}");
            }
        }
    }
}