using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reflection.Metadata.Ecma335;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Autonoceptor.Service.Hardware;
using Autonoceptor.Shared;
using Autonoceptor.Shared.Utilities;
using Nito.AsyncEx;
using NLog;

namespace Autonoceptor.Host
{
    public class LidarNavOverride : Car
    {
        private ILogger _logger = LogManager.GetCurrentClassLogger();

        private IDisposable _lidarDataDisposable;

        private const ushort _lidarServoChannel = 17;

        private const int _rightPwm = 1056;
        private const int _rightMidPwm = 1371;
        private const int _centerPwm = 1472;
        private const int _leftMidPwm = 1572;
        private const int _leftPwm = 1880;

        //These values are going to have to be environment based. Perhaps have a way for it to discover these values? 
        private int _maxStrength;
        private int _minStrength;

        private int _dangerDistance = 140;
        //-------------------------------------------------------

        private readonly AsyncLock _asyncLock = new AsyncLock();

        protected LidarNavOverride(CancellationTokenSource cancellationTokenSource, string brokerHostnameOrIp) 
            : base(cancellationTokenSource, brokerHostnameOrIp)
        {
            _isDangerZone.Add(Zone.Left, false);
            _isDangerZone.Add(Zone.Center, false);
            _isDangerZone.Add(Zone.Right, false);
        }

        private static DisplayGroup _displayGroup;

        protected new  void DisposeLcdWriters()
        {
            _lidarLcdDisposable?.Dispose();
            _lidarDataDisposable = null;

            base.DisposeLcdWriters();
        }

        protected new async Task ConfigureLcdWriters()
        {
            await base.ConfigureLcdWriters();

            var displayGroup = new DisplayGroup
            {
                DisplayItems = new Dictionary<int, string> { { 1, "Init Lidar nav" }, { 2, "Complete" } },
                GroupName = "LidarNav"
            };

            _displayGroup = await Lcd.AddDisplayGroup(displayGroup);

            _lidarLcdDisposable = Lidar.GetObservable()
                .ObserveOnDispatcher()
                .Where(d => d.IsValid)
                .Sample(TimeSpan.FromMilliseconds(250))
                .Subscribe(async data =>
                {
                    if (_displayGroup == null)
                        return;

                    _displayGroup.DisplayItems = new Dictionary<int, string>
                    {
                        {1, $"D: {data.Distance}"},
                        {2, $"S: {data.Strength}"},
                    };

                    await Lcd.UpdateDisplayGroup(_displayGroup);
                });
        }

        protected void DisposeLidarSweep()
        {
            _lidarDataDisposable?.Dispose();
            _lidarDataDisposable = null;
        }

        protected void EnableLidarSweep()
        {
            _lidarDataDisposable = Observable
                .Interval(TimeSpan.FromSeconds(3)) //At 2+ feet per second, this scans the center zone every 6 feet.
                .ObserveOnDispatcher()
                .Subscribe(async _ =>
                {
                    try
                    {
                        var data = await Sweep(Host.Sweep.Center);

                        if (data.Where(d => d.IsValid).Average(d => d.Distance) < _dangerDistance) //is the zone considered dangerous?
                        {
                            _isDangerZone[Zone.Center] = true;
                        }
                        else
                        {
                            _isDangerZone[Zone.Center] = false;//Zone is safe
                        }

                        if (_isDangerZone[Zone.Center]) //Check next zone
                        {
                            data = await Sweep(Host.Sweep.Right);
                        }
                        else
                        {
                            //Center zone is safe, so no need to scan any other zone
                            //reset other zones
                            _isDangerZone[Zone.Left] = false;
                            _isDangerZone[Zone.Right] = false;

                            await SetChannelValue(_centerPwm, _lidarServoChannel);

                            //TODO: Nothing to do here, proceed as we were

                            return;
                        }

                        if (data.Where(d => d.IsValid).Average(d => d.Distance) < _dangerDistance) //is the zone considered dangerous?
                        {
                            _isDangerZone[Zone.Right] = true;
                        }
                        else
                        {
                            //TODO: Turn right, because it is safe eh! 

                            return;
                        }

                        data = await Sweep(Host.Sweep.Left); //If We get here, the Center and Right zone are considered dangerous

                        if (data.Where(d => d.IsValid).Average(d => d.Distance) < _dangerDistance)
                        {
                            _isDangerZone[Zone.Left] = true;

                            //TODO: All zones are considered dangerous, maybe shoot one of the rockets and scan again?

                            //TODO: Or enter ramming mode? Better yet, Kamikaze mode! 

                        }
                    }
                    catch (Exception err)
                    {
                        _logger.Log(LogLevel.Error, "Error in EnableLidarSweep");
                        _logger.Log(LogLevel.Error, err.Message);
                    }

                });
        }

        protected new async Task InitializeAsync()
        {
            await base.InitializeAsync();
        }

        private readonly Dictionary<Zone,bool> _isDangerZone = new Dictionary<Zone, bool>();
        private IDisposable _lidarLcdDisposable;

        public async Task<List<LidarData>> Sweep(Sweep sweep)
        {
            try
            {
                using (await _asyncLock.LockAsync())
                {
                    var data = await SweepInternal(sweep);

                    await SetChannelValue(0, _lidarServoChannel); //Turn servo off

                    return await Task.FromResult(data);
                }
            }
            catch (Exception err)
            {
                _logger.Log(LogLevel.Error, "Error in Sweep");
                _logger.Log(LogLevel.Error, err.Message);
                return await Task.FromResult(new List<LidarData>(null));
            }
        }

        private async Task<List<LidarData>> SweepInternal(Sweep sweep)
        {
            try
            {
                var data = new List<LidarData>();
            
                switch (sweep)
                {
                    case Host.Sweep.Left:
                    {
                        for (var pwm = _leftMidPwm; pwm < _leftPwm; pwm += 10)
                        {
                            await SetChannelValue(pwm * 4, _lidarServoChannel);

                            var lidarData = await Lidar.GetLatest();

                            if (!lidarData.IsValid)
                                continue;

                            lidarData.Angle = Math.Round(pwm.Map(_leftMidPwm, _leftPwm, 0, -45));
                            data.Add(lidarData);
                        }

                        break;
                    }
                    case Host.Sweep.Right:
                    {
                        for (var pwm = _rightMidPwm; pwm > _rightPwm; pwm -= 10)
                        {
                            await SetChannelValue(pwm * 4, _lidarServoChannel);

                            var lidarData = await Lidar.GetLatest();

                            if (!lidarData.IsValid)
                                continue;

                            lidarData.Angle = Math.Round(pwm.Map(_rightMidPwm, _rightPwm, 0, 45)); ;
                            data.Add(lidarData);
                        }

                        break;
                    }
                    case Host.Sweep.Center:
                    {
                        // sweep left 15 degrees
                        for (var pwm = _centerPwm; pwm < _leftMidPwm; pwm += 10)
                        {
                            await SetChannelValue(pwm * 4, _lidarServoChannel);

                            var lidarData = await Lidar.GetLatest();

                            if (!lidarData.IsValid)
                                continue;

                            lidarData.Angle = Math.Round(pwm.Map(_centerPwm, _leftMidPwm, 0, -15));
                            data.Add(lidarData);
                        }

                        await Task.Delay(250);

                        // sweep right 30 degrees
                        for (var pwm = _leftMidPwm; pwm > _rightMidPwm; pwm -= 10)
                        {
                            await SetChannelValue(pwm * 4, _lidarServoChannel);

                            var lidarData = await Lidar.GetLatest();

                            if (!lidarData.IsValid)
                                continue;

                            lidarData.Angle = Math.Round(pwm.Map(_leftMidPwm, _rightMidPwm, -15, 15));
                            data.Add(lidarData);
                        }

                        break;
                    }
                }

                await Task.Delay(500);

                await SetChannelValue(_centerPwm * 4, _lidarServoChannel);

                await Task.Delay(500);

                return await Task.FromResult(data);
            }
            catch (Exception err)
            {
                _logger.Log(LogLevel.Error, "Error in SweepInternal");
                _logger.Log(LogLevel.Error, err.Message);
                return await Task.FromResult(new List<LidarData>(null));
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

                var steerValue = CenterPwm * 4;

                if (magnitude > 100)
                    magnitude = 100;

                //var correction = CheckDangerZone(direction, magnitude);

                //if (correction.Item1 != direction)
                //{
                //    direction = correction.Item1;
                //    magnitude = correction.Item2;
                //}

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
                _logger.Log(LogLevel.Error, "Error in SetVehicleHeading");
                _logger.Log(LogLevel.Error, err); 
            }
}

        protected Tuple<SteeringDirection, double> CheckDangerZone(SteeringDirection direction, double magnitude)
        {
            try
            {
                Tuple<SteeringDirection, double> newDirectionMagnitude = null;

                //TODO: Finish this, just logging for now
                //This is where you would override to steer us out of danger
                if (_isDangerZone.Any(z => z.Value))
                {
                    foreach (var zone in _isDangerZone)
                    {
                        _logger.Log(LogLevel.Info, $"Zone {zone.Key} danger: {zone.Value}");
                    }

                    var safeZone = _isDangerZone.FirstOrDefault(zone => !zone.Value);

                    switch (safeZone.Key)
                    {
                        case Zone.Center:
                            newDirectionMagnitude = new Tuple<SteeringDirection, double>(SteeringDirection.Center, 100);

                            break;
                        case Zone.Left:
                            newDirectionMagnitude = new Tuple<SteeringDirection, double>(SteeringDirection.Left, 100);

                            break;
                        case Zone.Right:
                            newDirectionMagnitude = new Tuple<SteeringDirection, double>(SteeringDirection.Right, 100);

                            break;
                    }

                    //await base.SetChannelValue(value, channel); //steer towards safe zone
                    //await base.SetChannelValue(value, MovementChannel); //Maybe slow down as well?
                }

                return newDirectionMagnitude;
            }
            catch (Exception err)
            {
                _logger.Log(LogLevel.Error, "Error in CheckDangerZone");
                _logger.Log(LogLevel.Error, err);
                return new Tuple<SteeringDirection, double>(SteeringDirection.Left, 100);
            }
        }

        
    }

    public enum Sweep
    {
        Left,
        Right,
        Full,
        Center //Continually sweep 15 degrees(?) directly in front of the car
    }

    public enum Zone //I really wanted to call this "Danger Zone"
    {
        Left,
        Center,
        Right
    }

    
}