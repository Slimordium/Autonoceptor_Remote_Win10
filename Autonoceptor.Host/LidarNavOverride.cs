using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
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
        private const int _rightMidPwm = 1322;
        private const int _centerPwm = 1472;
        private const int _leftMidPwm = 1622;
        private const int _leftPwm = 1880;

        //These values are going to have to be environment based. Perhaps have a way for it to discover these values? 
        private int _maxStrength;
        private int _minStrength;

        private int _dangerDistance = 400;
        //-------------------------------------------------------

        private readonly AsyncLock _asyncLock = new AsyncLock();

        protected LidarNavOverride(CancellationTokenSource cancellationTokenSource, string brokerHostnameOrIp) 
            : base(cancellationTokenSource, brokerHostnameOrIp)
        {
            _isDangerZone.Add(Zone.Left, false);
            _isDangerZone.Add(Zone.Center, false);
            _isDangerZone.Add(Zone.Right, false);
        }

        //TODO: Implement servo sweep, query servo position, and associating servo position with lidar data as a sort of radar sweep. 
        protected new async Task InitializeAsync()
        {
            await base.InitializeAsync();

            return;

            _lidarDataDisposable = Observable
                .Interval(TimeSpan.FromSeconds(2)) //At 2+ feet per second, this scans the center zone every 4 feet.
                .ObserveOnDispatcher()
                .Subscribe(async _ =>
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

                        return; 
                    }

                    if (data.Where(d => d.IsValid).Average(d => d.Distance) < _dangerDistance) //is the zone considered dangerous?
                    {
                        _isDangerZone[Zone.Right] = true;
                    }

                    if (_isDangerZone[Zone.Right]) //Check next zone
                    {
                        data = await Sweep(Host.Sweep.Left);
                    }
                    else
                    {
                        _isDangerZone[Zone.Left] = false; //Reset to default
                        return; //Center zone is not safe, right zone is safe
                    }

                    if (data.Where(d => d.IsValid).Average(d => d.Distance) < _dangerDistance) //is the zone considered dangerous?
                    {
                        _isDangerZone[Zone.Left] = true;
                    }
                    else
                    {
                        _isDangerZone[Zone.Left] = false;//Zone is safe
                    }

                    //If we get here, we are screwed because all zones are dangerous. 
                });
        }

        private readonly Dictionary<Zone,bool> _isDangerZone = new Dictionary<Zone, bool>();

        public async Task<List<LidarData>> Sweep(Sweep sweep)
        {
            using (await _asyncLock.LockAsync())
            {
                var data = await SweepInternal(sweep);

                await SetChannelValue(0, _lidarServoChannel); //Turn servo off

                return await Task.FromResult(data);
            }
        }

        private async Task<List<LidarData>> SweepInternal(Sweep sweep)
        {
            var data = new List<LidarData>();
            
            switch (sweep)
            {
                case Host.Sweep.Left:
                {
                    for (var pwm = _centerPwm; pwm < _leftPwm; pwm += 10)
                    {
                        await SetChannelValue(pwm * 4, _lidarServoChannel);

                        var lidarData = await Lidar.GetLatest();
                        lidarData.Angle = pwm.Map(_centerPwm, _leftPwm, 0, -45);
                        data.Add(lidarData);
                    }

                    break;
                }
                case Host.Sweep.Right:
                {
                    for (var pwm = _centerPwm; pwm > _rightPwm; pwm -= 10)
                    {
                        await SetChannelValue(pwm * 4, _lidarServoChannel);

                        var lidarData = await Lidar.GetLatest();
                        lidarData.Angle = pwm.Map(_centerPwm, _rightPwm, 0, 45); ;
                        data.Add(lidarData);
                    }

                    break;
                }
                case Host.Sweep.Center:
                {
                    for (var pwm = _leftMidPwm; pwm > _rightMidPwm; pwm += 10)
                    {
                        await SetChannelValue(pwm * 4, _lidarServoChannel);

                        var lidarData = await Lidar.GetLatest();
                        lidarData.Angle = pwm.Map(_leftMidPwm, _rightMidPwm, -15, 15);

                        data.Add(lidarData);
                    }

                    //var validDataPoints = data.Where(d => d.Distance < )

                    break;
                }
            }

            await Task.Delay(500);

            await SetChannelValue(_centerPwm * 4, _lidarServoChannel);

            await Task.Delay(250);

            return await Task.FromResult(data);
        }

        protected new async Task SetChannelValue(int value, ushort channel)
        {
            if (Stopped && (channel == MovementChannel || channel == SteeringChannel))
            {
                await base.SetChannelValue(StoppedPwm * 4, MovementChannel);
                await base.SetChannelValue(0, SteeringChannel);
                return;
            }

            //TODO: Finish this, just logging for now
            //This is where you would override to steer us out of danger
            if (channel == SteeringChannel && _isDangerZone.Any(z => z.Value))
            {
                var dangerZone = _isDangerZone.Where(zone => zone.Value);

                foreach (var z in _isDangerZone)
                {
                    _logger.Log(LogLevel.Info, $"Zone {z.Key} danger: {z.Value}");
                }

                //await base.SetChannelValue(value, channel); //steer towards safe zone
                //await base.SetChannelValue(value, MovementChannel); //Maybe slow down as well?
            }
            
            //else
            //{
            await base.SetChannelValue(value, channel);
            //}
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