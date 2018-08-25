using System;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autonoceptor.Shared;
using Autonoceptor.Shared.Utilities;

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
                    //var location = await PwmController.GetChannelValue(_lidarServoChannel);
                    await UpdateLidarNavOverride(lidarData, 0);// location value goes here
                });
        }

        private async Task UpdateLidarNavOverride(LidarData lidarData, int location) //Implement 2D map. Modify "write to hardware method" to with values to avoid ?
        {
            if (lidarData.Distance < 40)
            {
                await EmergencyBrake();
            }
        }

        //TODO: Override these PWM values with modified ones based on obsticle. May need to pass move request in here instead, so we know what direction is being attempted
        protected async Task WriteToHardware(ushort steeringPwm, ushort movePwm)
        {
            await PwmController.SetChannelValue(steeringPwm, SteeringChannel);

            await PwmController.SetChannelValue(movePwm, MovementChannel);
        }
    }
}