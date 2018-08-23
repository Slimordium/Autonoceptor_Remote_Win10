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
        private IDisposable _lidarServoDisposable;

        private const ushort _lidarServoChannel = 18;

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
                .ObserveOnDispatcher()
                .Subscribe(async lidarData =>
                {
                    await UpdateLidarNavOverride(lidarData);
                });

            //_lidarServoDisposable = Observable
            //    .Interval(TimeSpan.FromMilliseconds(10))
            //    .ObserveOnDispatcher()
            //    .Subscribe(_ =>
            //    {

            //    });
        }

        private async Task UpdateLidarNavOverride(LidarData lidarData)
        {
            if (lidarData.Distance < 60)
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