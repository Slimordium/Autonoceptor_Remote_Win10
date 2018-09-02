using System;
using System.Threading.Tasks;
using Nito.AsyncEx;

namespace Autonoceptor.Host
{
    public class GpsNavParameters
    {
        private readonly AsyncLock _asyncPpiLock = new AsyncLock();
        private readonly AsyncLock _asyncMoveLock = new AsyncLock();
        private readonly AsyncLock _asyncHeadingLock = new AsyncLock();

        private double _targetHeading;
        private double _currentHeading;
        private double _currentPpi;
        private double _lastMoveMagnitude;
        private double _distanceToWaypoint;

        public async Task<double> GetDistanceToWaypoint()
        {
            using (await _asyncLock.LockAsync())
            {
                return _distanceToWaypoint;
            }
        }

        public async Task SetDistanceToWaypoint(double distanceToTarget)
        {
            using (await _asyncLock.LockAsync())
            {
                _distanceToWaypoint = distanceToTarget;
            }
        }

        public async Task<double> GetTargetPpi()
        {
            using (await _asyncPpiLock.LockAsync())
            {
                return _currentPpi;
            }
        }

        public async Task SetTargetPpi(double ppi)
        {
            using (await _asyncPpiLock.LockAsync())
            {
                _currentPpi = ppi;
            }
        }

        public async Task<double> GetLastMoveMagnitude()
        {
            using (await _asyncMoveLock.LockAsync())
            {
                return _lastMoveMagnitude;
            }
        }

        public async Task SetLastMoveMagnitude(double heading)
        {
            using (await _asyncMoveLock.LockAsync())
            {
                _lastMoveMagnitude = heading;
            }
        }

        public async Task<double> GetTargetHeading()
        {
            using (await _asyncHeadingLock.LockAsync())
            {
                return _targetHeading;
            }
        }

        public async Task SetTargetHeading(double heading)
        {
            using (await _asyncHeadingLock.LockAsync())
            {
                _targetHeading = heading;
            }
        }

        public async Task<double> GetCurrentHeading()
        {
            using (await _asyncHeadingLock.LockAsync())
            {
                return _currentHeading;
            }
        }

        public async Task SetCurrentHeading(double heading)
        {
            using (await _asyncHeadingLock.LockAsync())
            {
                _currentHeading = heading;
            }
        }

        public async Task<double> GetSteeringMagnitude()
        {
            var diff = Math.Abs(await GetCurrentHeading() - await GetTargetHeading());

            var maxdif = 22 - 10 * Math.Atan((_distanceToWaypoint - 10) / 3);

            if (diff > maxdif) //Can turn about 45 degrees 
            {
                diff = maxdif;
            }
                

            return diff;
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