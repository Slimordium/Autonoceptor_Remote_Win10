using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using Autonoceptor.Shared;
using Autonoceptor.Shared.Utilities;
using Nito.AsyncEx;

namespace Autonoceptor.Host
{
    public class LidarNavOverride : Car
    {
        private IDisposable _lidarDataDisposable;

        private const ushort _lidarServoChannel = 17;

        private bool toggle;

        private LidarBehaviourMode _mode = LidarBehaviourMode.Passive;
        private AngleDataPoints _dataPoints = new AngleDataPoints();

        private const int _rightPwm = 1056;
        private const int _centerPwm = 1486;
        private const int _leftPwm = 1880;

        private readonly AsyncLock _asyncLock = new AsyncLock();

        protected LidarNavOverride(CancellationTokenSource cancellationTokenSource, string brokerHostnameOrIp) 
            : base(cancellationTokenSource, brokerHostnameOrIp)
        {
        }

        //TODO: Implement servo sweep, query servo position, and associating servo position with lidar data as a sort of radar sweep. 
        protected new async Task InitializeAsync()
        {
            await base.InitializeAsync();

            _lidarDataDisposable = Lidar
                .GetObservable()
                .Sample(TimeSpan.FromMilliseconds(100))
                .ObserveOnDispatcher()
                .Subscribe(async lidarData =>
                {
                    await UpdateLidarNavOverride(lidarData);
                });
        }

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

            if (sweep == Host.Sweep.Left)
            {
                for (var pwm = _centerPwm; pwm < _leftPwm; pwm += 10)
                {
                    await SetChannelValue(pwm * 4, _lidarServoChannel);

                    var lidarData = await Lidar.GetLatest();
                    lidarData.Angle = pwm.Map(_centerPwm, _leftPwm, 0, -45);
                    data.Add(lidarData);
                }
            }

            if (sweep == Host.Sweep.Right)
            {
                for (var pwm = _centerPwm; pwm > _rightPwm; pwm -= 10)
                {
                    await SetChannelValue(pwm * 4, _lidarServoChannel);

                    var lidarData = await Lidar.GetLatest();
                    lidarData.Angle = pwm.Map(_centerPwm, _rightPwm, 0, 45); ;
                    data.Add(lidarData);
                }
            }

            await Task.Delay(500);

            await SetChannelValue(_centerPwm * 4, _lidarServoChannel);

            await Task.Delay(250);

            return await Task.FromResult(data);
        }

        //TODO: You can add waypoints that take precedence over the current one using Waypoints.AddFirst(wayPoint goes here)

        //TODO: Sweep here?
        private async Task UpdateLidarNavOverride(LidarData lidarData) //Implement 2D map. Modify "write to hardware method" to with values to avoid ?
        {

            switch (_mode)
            {
                case LidarBehaviourMode.Passive:
                    if (lidarData.Distance < 60)
                    {
                        _mode = LidarBehaviourMode.ObsticleDefinitionCorse;
                        await EmergencyBrake();
                    }
                    break;
                case LidarBehaviourMode.ObsticleDefinitionCorse:



                    // If I have all of my data 

                    break;
                case LidarBehaviourMode.ObsticleAvoidance:
                    break;
                default:
                    break;
            }
        }

        //TODO: Pass move request into this method. Make turn amount uniform
        protected async Task WriteToHardware(ushort steeringPwm, ushort movePwm)
        {
            await SetChannelValue(steeringPwm, SteeringChannel);

            await SetChannelValue(movePwm, MovementChannel);
        }

        private double pwmToDegrees(UInt16 pwmvalue)
        {
            return ((pwmvalue - _centerPwm) / (_leftPwm - _centerPwm)) * (100 / 45);
        }
    }

    public enum Sweep
    {
        Left,
        Right,
        Full,
        Center
    }

    public enum LidarBehaviourMode
    {
        Passive,
        ObsticleDefinitionCorse,
        ObsticleDefinitionFine,
        ObsticleAvoidance
    }

    
}