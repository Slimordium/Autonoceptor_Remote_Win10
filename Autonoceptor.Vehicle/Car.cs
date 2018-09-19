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

        protected const int RightLidarPwm = 1056;
        private const int _rightMidLidarPwm = 1282;
        protected const int CenterLidarPwm = 1488;
        private const int _leftMidLidarPwm = 1682;
        protected const int LeftLidarPwm = 1880;

        private const int _nerfDartChannel = 15;

        private int _safeDistance = 90;

        private Thread _lidarThread;

        private Thread _cruiseControlThread;

        private List<IDisposable> _sensorDisposables = new List<IDisposable>();

        private IDisposable _odoLcdDisposable;

        private readonly ManualResetEventSlim _manualResetEventSlimHeadTorque = new ManualResetEventSlim(false);
        private readonly ManualResetEventSlim _manualResetEventSlimHead = new ManualResetEventSlim(false);

        public WaypointQueue Waypoints { get; set; }

        protected string BrokerHostnameOrIp { get; set; }

        private volatile int _overrideMoveMagnitude = -1;

        private bool _danger;
        protected bool Danger
        {
            get => Volatile.Read(ref _danger);
            set => Volatile.Write(ref _danger, value);
        }

        private CancellationTokenSource _cruiseControlCancellationTokenSource = new CancellationTokenSource();
        private IDisposable _lidarLcdDisposable;

        public CancellationTokenSource LidarCancellationTokenSource { get; set; } = new CancellationTokenSource();

        public int SafeDistance
        {
            get => Volatile.Read(ref _safeDistance);
            set
            {
                var val = value;

                if (val < 30)
                    val = 30;

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

            await Lcd.Update(GroupName.LidarDangerSet, "Safe zone", $" {SafeDistance}cm");
            await Lcd.SetUpCallback(GroupName.LidarDangerSet, IncrementSafeDistance);
            await Lcd.SetDownCallback(GroupName.LidarDangerSet, DecrementSafeDistance);

            await Lcd.Update(GroupName.GpsNavSpeed, "Nav feet p/sec", $" {_fpsTarget} fps");
            await Lcd.SetUpCallback(GroupName.GpsNavSpeed, IncrementSpeed);
            await Lcd.SetDownCallback(GroupName.GpsNavSpeed, DecrementSpeed);

            await Lcd.Update(GroupName.LidarEnable, "Lidar enable", string.Empty);
            await Lcd.SetUpCallback(GroupName.LidarEnable, LidarEnable);
            await Lcd.SetDownCallback(GroupName.LidarEnable, LidarDisable);

            StartLidarThread();
        }

        private bool _lidarEnabled;

        private string LidarEnable()
        {
            Volatile.Write(ref _lidarEnabled, true);

            return "Enabled";
        }

        private string LidarDisable()
        {
            Volatile.Write(ref _lidarEnabled, false);

            return "Disabled";
        }

        private string IncrementSafeDistance()
        {
            var sd = SafeDistance;

            sd = sd + 2;

            if (sd > 500)
                sd = 500;

            SafeDistance = sd;

            return $" {sd}cm";
        }

        private string DecrementSafeDistance()
        {
            var sd = SafeDistance;

            sd = sd - 2;

            if (sd < 35)
                sd = 35;

            SafeDistance = sd;

            return $" {sd}cm";
        }

        private string IncrementSpeed()
        {
            var fpsTarget = _fpsTarget;

            fpsTarget = fpsTarget + .25;

            if (fpsTarget > 5)
                fpsTarget = 5;

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
                        var ir = await SetChannelValue(CenterLidarPwm * 4, LidarServoChannel);

                        await Task.Delay(250);

                        var originalTargetFps = _fpsTarget;

                        while (!LidarCancellationTokenSource.IsCancellationRequested)
                        {
                            var safeDistance = SafeDistance;

                            try
                            {
                                if (Stopped || !Volatile.Read(ref _lidarEnabled))
                                {
                                    await Task.Delay(500);
                                    continue;
                                }

                                var lidarData = await Lidar.GetLatest();

                                if (!lidarData.IsValid)
                                {
                                    continue;
                                }

                                if (lidarData.Distance < safeDistance)
                                {
                                    //Danger
                                    Danger = true;

                                    await UpdateCruiseControl(1.5);

                                    await Lcd.Update(GroupName.LidarDangerZone, $"Danger @ {lidarData.Distance}cm", string.Empty, true);
                                }
                                else
                                {
                                    Danger = false;

                                    //Safe
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

                        await Lcd.Update(GroupName.Odometer, $"Cruise started", $" {_fpsTarget} fps", true);

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
                                moveMagnitude = moveMagnitude + 10;

                            if (currentFps > fpsTarget)
                            {
                                starting = false;

                                //if (moveMagnitude > 60)
                                //{
                                //    moveMagnitude = moveMagnitude - .7;
                                //}
                                //else if (moveMagnitude > 40)
                                //{
                                //    moveMagnitude = moveMagnitude - .6;
                                //}
                                //else
                                //{
                                moveMagnitude = moveMagnitude - .5;
                                //}
                            }

                            if (moveMagnitude > 70)
                                moveMagnitude = 70;

                            if (moveMagnitude < 0)
                                moveMagnitude = 0;

                            if (Stopped)
                                return;

                            //if (!isStuck)
                            //{
                                  await SetVehicleTorque(MovementDirection.Forward, moveMagnitude);
                            //}
                            //else
                            //{
                            //    await Lcd.Update(GroupName.Odometer, $"Stuck!", string.Empty);

                            //    // shoot a nerf dart
                            //    //await SetChannelValue(1500 * 4, 15);

                            //    // turn wheels slightly to the left
                            //    await SetVehicleHeading(SteeringDirection.Left, 70);

                            //    // reverse
                            //    await SetVehicleTorque(MovementDirection.Reverse, 60);
                            //    await Task.Delay(1800);

                            //    // now continue trying to get to next waypoint
                            //    await SetVehicleTorque(MovementDirection.Forward, 60);

                            //    isStuck = false;
                            //}
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

            if (magnitude < 0)
                magnitude = 0;

            if (Danger && direction == MovementDirection.Forward)
            {
                magnitude = 2;
            }

            switch (direction)
            {
                case MovementDirection.Forward:
                    moveValue = Convert.ToUInt16(magnitude.Map(0, 100, StoppedPwm, ForwardPwm)) * 4;
                    break;
                case MovementDirection.Reverse:
                    moveValue = Convert.ToUInt16(magnitude.Map(0, 100, StoppedPwm, ReversePwm)) * 4;
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

            await SetVehicleTorque(MovementDirection.Forward, 70);

            await Task.Delay(100);

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

                await Lcd.Update(GroupName.Car, "Started", string.Empty);

                return;
            }

            await SetChannelValue(StoppedPwm * 4, MovementChannel);
            await SetChannelValue(0, SteeringChannel);

            if (Stopped)
                return;

            Stopped = true;

            await Lcd.Update(GroupName.Car, "Stopped", string.Empty);
        }

        public async Task DisableServos()
        {
            await SetChannelValue(0, MovementChannel);
            await SetChannelValue(0, SteeringChannel);
        }

        private async Task<Tuple<SteeringDirection, double>> FindSafeHeading()
        {
            await Stop();

            var leftData = new List<LidarData>();
            var rightData = new List<LidarData>();

            //Very basic, is left safe, or right?
            for (var pwm = LeftLidarPwm; pwm > CenterLidarPwm; pwm = pwm - 5)
            {
                var cv = await SetChannelValue(pwm * 4, LidarServoChannel);
                var data = await Lidar.GetLatest();
                data.Angle = Math.Round(pwm.Map(CenterLidarPwm, LeftLidarPwm, 0, 45));
                leftData.Add(data);
            }

            for (var pwm = CenterLidarPwm; pwm > RightLidarPwm; pwm = pwm - 5)
            {
                var cv = await SetChannelValue(pwm * 4, LidarServoChannel);
                var data = await Lidar.GetLatest();
                data.Angle = Math.Round(pwm.Map(RightLidarPwm, CenterLidarPwm, 0, 45));
                rightData.Add(data);
            }

            var leftSafeAngles = leftData.Where(d => d.Distance > SafeDistance).ToList();
            var rightSafeAngles = rightData.Where(d => d.Distance > SafeDistance).ToList();

            await Stop(true);

            if (leftSafeAngles.Count <= 10 && rightSafeAngles.Count <= 10)
            {
                return await Task.FromResult(new Tuple<SteeringDirection, double>(SteeringDirection.None, 0)); //The sky is falling!
            }

            if (leftSafeAngles.Count > rightSafeAngles.Count)
            {
                var lSafeMagnitude = Math.Round(leftData.Average(d => d.Angle).Map(0, 45, 150, 0));

                var scv = await SetChannelValue(_leftMidLidarPwm * 4, LidarServoChannel);
                return await Task.FromResult(new Tuple<SteeringDirection, double>(SteeringDirection.Left, lSafeMagnitude));
            }

            var rSafeMagnitude = Math.Round(rightSafeAngles.Average(d => d.Angle).Map(0, 45, 150, 0));

            var ccv = await SetChannelValue(_rightMidLidarPwm * 4, LidarServoChannel);
            return await Task.FromResult(new Tuple<SteeringDirection, double>(SteeringDirection.Right, rSafeMagnitude));
        }

        protected async Task<bool> SetVehicleHeadingAndTorque(SteeringDirection steeringDirection, double steeringMagnitude, MovementDirection movementDirection, double movementMagnitude)
        {
            if (Stopped)
            {
                var mcv = await SetChannelValue(StoppedPwm * 4, MovementChannel);
                var scv = await SetChannelValue(0, SteeringChannel);
                var lcv = await SetChannelValue(0, LidarServoChannel);

                return await Task.FromResult(false);
            }

            if (_manualResetEventSlimHeadTorque.IsSet)
                return await Task.FromResult(false);

            _manualResetEventSlimHeadTorque.Set();

            if (steeringMagnitude > 100) steeringMagnitude = 100;
            if (steeringMagnitude < 0) steeringMagnitude = 0;
            if (movementMagnitude > 100) movementMagnitude = 100;
            if (movementMagnitude < 0) movementMagnitude = 0;

            var noSafeHeading = false;

            if (Danger && movementDirection == MovementDirection.Forward)
            {
                steeringMagnitude = steeringMagnitude + 30; //This seems like a must for now

                switch (steeringDirection)
                {
                    case SteeringDirection.Left:
                        steeringDirection = SteeringDirection.Right;
                        break;
                    case SteeringDirection.Right:
                        steeringDirection = SteeringDirection.Left;
                        break;
                    case SteeringDirection.Center:

                        var safeDirectionAndMagnitude = await FindSafeHeading();
                        steeringDirection = safeDirectionAndMagnitude.Item1;

                        if (steeringDirection == SteeringDirection.None)
                        {
                            noSafeHeading = true;
                        }
                        else
                        {
                            steeringMagnitude = 100;//safeDirectionAndMagnitude.Item2;
                        }
                        
                        break;
                }
            }

            var steerValue = CenterSteeringPwm * 4;
            var lidarAngle = CenterLidarPwm * 4;
            var moveValue = StoppedPwm * 4;

            try
            {
                switch (steeringDirection)
                {
                    case SteeringDirection.Left:
                        steerValue = Convert.ToUInt16(steeringMagnitude.Map(0, 100, CenterSteeringPwm, LeftSteeringPwm)) * 4;
                        lidarAngle = Convert.ToUInt16(steeringMagnitude.Map(0, 100, CenterLidarPwm, LeftLidarPwm)) * 4;
                        break;
                    case SteeringDirection.Right:
                        steerValue = Convert.ToUInt16(steeringMagnitude.Map(0, 100, CenterSteeringPwm, RightSteeringPwm)) * 4;
                        lidarAngle = Convert.ToUInt16(steeringMagnitude.Map(0, 100, CenterLidarPwm, RightLidarPwm)) * 4;
                        break;
                }

                if (steeringDirection != SteeringDirection.None)
                {
                    switch (movementDirection)
                    {
                        case MovementDirection.Forward:
                            moveValue = Convert.ToUInt16(movementMagnitude.Map(0, 100, StoppedPwm, ForwardPwm)) * 4;
                            break;
                        case MovementDirection.Reverse:
                            moveValue = Convert.ToUInt16(movementMagnitude.Map(0, 100, StoppedPwm, ReversePwm)) * 4;
                            break;
                    }
                }

                if (!Danger)
                {
                    var rsv = await SetChannelValue(lidarAngle, LidarServoChannel);
                }

                var cv = await SetChannelValue(steerValue, SteeringChannel);

                if (noSafeHeading && movementDirection == MovementDirection.Forward)
                {
                    await Stop();
                    await Lcd.Update(GroupName.Car, "No safe heading!", string.Empty, true);
                }
                else
                {
                    var scv = await SetChannelValue(moveValue, MovementChannel);
                }
            }
            catch (Exception e)
            {
                _logger.Log(LogLevel.Error, e.Message);

                return await Task.FromResult(false);
            }
            finally
            {
                _manualResetEventSlimHeadTorque.Reset();
            }

            return await Task.FromResult(true);
        }

        protected async Task<bool> SetVehicleHeading(SteeringDirection direction, double magnitude)
        {
            try
            {
                if (Stopped)
                {
                    var mcv = await SetChannelValue(StoppedPwm * 4, MovementChannel);
                    var scv = await SetChannelValue(0, SteeringChannel);
                    var lcv = await SetChannelValue(0, LidarServoChannel);

                    return await Task.FromResult(false);
                }

                if (_manualResetEventSlimHead.IsSet)
                    return await Task.FromResult(false);

                _manualResetEventSlimHead.Set();

                if (Danger)
                {
                    magnitude = magnitude + 30; //This seems like a must for now

                    switch (direction)
                    {
                        case SteeringDirection.Left:
                            direction = SteeringDirection.Right;
                            break;
                        case SteeringDirection.Right:
                            direction = SteeringDirection.Left;
                            break;
                        case SteeringDirection.Center:
                            var safeDirectionAndMagnitude = await FindSafeHeading();
                            direction = safeDirectionAndMagnitude.Item1;

                            if (direction == SteeringDirection.None)
                            {
                                await Lcd.Update(GroupName.Car, "No safe heading found!", string.Empty, true);
                                await Stop();
                            }
                            //magnitude = 100;//safeDirectionAndMagnitude.Item2;
                            break;
                    }
                }

                var steerPwmValue = CenterSteeringPwm * 4;
                var lidarPwmValue = CenterLidarPwm * 4;

                if (magnitude > 100) magnitude = 100;
                if (magnitude < 0) magnitude = 0;

                switch (direction)
                {
                    case SteeringDirection.Left:
                        steerPwmValue = Convert.ToUInt16(magnitude.Map(0, 100, CenterSteeringPwm, LeftSteeringPwm)) * 4;
                        lidarPwmValue = Convert.ToUInt16(magnitude.Map(0, 100, CenterLidarPwm, LeftLidarPwm)) * 4;
                        break;
                    case SteeringDirection.Right:
                        steerPwmValue = Convert.ToUInt16(magnitude.Map(0, 100, CenterSteeringPwm, RightSteeringPwm)) * 4;
                        lidarPwmValue = Convert.ToUInt16(magnitude.Map(0, 100, CenterLidarPwm, RightLidarPwm)) * 4;
                        break;
                }

                //If in danger, keep pointing the LIDAR at the danger zone
                if (!Danger)
                {
                    var rsv = await SetChannelValue(lidarPwmValue, LidarServoChannel); 
                }

                var cv = await SetChannelValue(steerPwmValue, SteeringChannel);
            }
            catch (Exception err)
            {
                _logger.Log(LogLevel.Error, $"Error in SetVehicleHeading: {err}");

                return await Task.FromResult(false);
            }
            finally
            {
                _manualResetEventSlimHead.Reset();
            }

            return await Task.FromResult(true);
        }
    }
}