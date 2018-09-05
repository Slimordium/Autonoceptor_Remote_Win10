using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Globalization.NumberFormatting;
using Autonoceptor.Shared;
using Nito.AsyncEx;

namespace Autonoceptor.Host
{
    public class LidarNavOverride : Car
    {
        private IDisposable _lidarDataDisposable;
        private IDisposable _sweepDisposable;

        private const ushort _lidarServoChannel = 17;

        private bool toggle;

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

            //_sweepDisposable = Observable
            //    .Interval(TimeSpan.FromMilliseconds(1500))
            //    .ObserveOnDispatcher()
            //    .Subscribe(async _ =>
            //    {
            //        if (toggle)
            //        {
            //            await PwmController.SetChannelValue(_rightPwm * 4, _lidarServoChannel);
            //        }
            //        else
            //        {
            //            await PwmController.SetChannelValue(_leftPwm * 4, _lidarServoChannel);
            //        }

            //        toggle = !toggle;
            //    });

            _lidarDataDisposable = Lidar
                .GetObservable()
                .ObserveOnDispatcher()
                .Subscribe(async lidarData =>
                {
                    await UpdateLidarNavOverride(lidarData);
                });
        }

        //TODO: Will this work?
        public async Task<List<LidarData>> Sweep(Sweep sweep)
        {
            using (await _asyncLock.LockAsync())
            {
                var data = new List<LidarData>();

                var disposable = Lidar.GetObservable()
                    .ObserveOnDispatcher()
                    .Sample(TimeSpan.FromMilliseconds(50))
                    .Subscribe(async d =>
                    {
                        var location = await GetChannelValue(_lidarServoChannel);

                        d.Angle = location;

                        data.Add(d);
                    });

                switch (sweep)
                {
                    case Host.Sweep.Center:
                        //Already moves to center after sweep is complete
                        break;
                    case Host.Sweep.Left:
                        await SetChannelValue(_leftPwm * 4, _lidarServoChannel);
                        break;
                    case Host.Sweep.Right:
                        await SetChannelValue(_rightPwm * 4, _lidarServoChannel);
                        break;
                    case Host.Sweep.Full:
                        await SetChannelValue(_leftPwm * 4, _lidarServoChannel);
                        await SetChannelValue(_rightPwm * 4, _lidarServoChannel);
                        break;
                }

                disposable.Dispose();
                disposable = null;

                await SetChannelValue(_centerPwm * 4, _lidarServoChannel);

                return await Task.FromResult(data);
            }
        }

        //TODO: You can add waypoints that take precedence over the current one using Waypoints.AddFirst(wayPoint goes here)

        //TODO: Sweep here?
        private async Task UpdateLidarNavOverride(LidarData lidarData) //Implement 2D map. Modify "write to hardware method" to with values to avoid ?
        {
            //if (lidarData.Distance < 40)
            //{
            //    await Stop();
            //}
        }

        //TODO: Pass move request into this method. Make turn amount uniform
        protected async Task WriteToHardware(ushort steeringPwm, ushort movePwm)
        {
            await SetChannelValue(steeringPwm, SteeringChannel);

            await SetChannelValue(movePwm, MovementChannel);
        }
    }

    public enum Sweep
    {
        Left,
        Right,
        Full,
        Center
    }
}