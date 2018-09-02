using System;
using System.Threading.Tasks;
using Nito.AsyncEx;

namespace Autonoceptor.Host
{
    public class GpsNavParameters
    {
        private AsyncLock _asyncLock = new AsyncLock();

        private double _targetHeading;
        private double _currentHeading;
        private double _currentPpi;
        private double _lastMoveMagnitude;

        public async Task<double> GetTargetPpi()
        {
            using (await _asyncLock.LockAsync())
            {
                return _currentPpi;
            }
        }

        public async Task SetTargetPpi(double ppi)
        {
            using (await _asyncLock.LockAsync())
            {
                _currentPpi = ppi;
            }
        }

        public async Task<double> GetLastMoveMagnitude()
        {
            using (await _asyncLock.LockAsync())
            {
                return _lastMoveMagnitude;
            }
        }

        public async Task SetLastMoveMagnitude(double heading)
        {
            using (await _asyncLock.LockAsync())
            {
                _lastMoveMagnitude = heading;
            }
        }

        public async Task<double> GetTargetHeading()
        {
            using (await _asyncLock.LockAsync())
            {
                return _targetHeading;
            }
        }

        public async Task SetTargetHeading(double heading)
        {
            using (await _asyncLock.LockAsync())
            {
                _targetHeading = heading;
            }
        }

        public async Task<double> GetCurrentHeading()
        {
            using (await _asyncLock.LockAsync())
            {
                return _currentHeading;
            }
        }

        public async Task SetCurrentHeading(double heading)
        {
            using (await _asyncLock.LockAsync())
            {
                _currentHeading = heading;
            }
        }

        public async Task<double> GetSteeringMagnitude()
        {
            var diff = await GetCurrentHeading() - await GetTargetHeading();

            var absDiff = Math.Abs(diff);

            if (absDiff > 25) //Can turn about 45 degrees 
                absDiff = 25;

            return absDiff;
        }

        public async Task<SteeringDirection> GetSteeringDirection()
        {
            SteeringDirection steerDirection;

            var diff = await GetCurrentHeading() - await GetTargetHeading();

            if (diff < 0)
            {
                steerDirection = Math.Abs(diff) > 180 ? SteeringDirection.Left : SteeringDirection.Right;
            }
            else
            {
                steerDirection = Math.Abs(diff) > 180 ? SteeringDirection.Right : SteeringDirection.Left;
            }

            return steerDirection;
        }
    }
}