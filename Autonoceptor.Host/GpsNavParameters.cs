using System;
using System.Threading.Tasks;
using Nito.AsyncEx;
using NLog;

namespace Autonoceptor.Host
{
    public class GpsNavParameters
    {
        private readonly AsyncLock _asyncPpiLock = new AsyncLock();
        private readonly AsyncLock _asyncMoveLock = new AsyncLock();
        private readonly AsyncLock _asyncHeadingLock = new AsyncLock();
        private readonly AsyncLock _asyncLock = new AsyncLock();

        private double _targetHeading;
        private double _currentHeading;
        private double _currentPpi;
        private double _lastMoveMagnitude;
        private double _distanceToWaypoint;

        private readonly ILogger _logger = LogManager.GetCurrentClassLogger();

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
                _distanceToWaypoint = distanceToTarget/12;
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
            

            _logger.Log(LogLevel.Info,$"Distance to target: {_distanceToWaypoint}");
            _logger.Log(LogLevel.Info,$"Diff: {diff}");

            var maxdif = 30 - 3 * Math.Atan((_distanceToWaypoint - 20) / 5);

            _logger.Log(LogLevel.Info,$"Maximum difference{maxdif}");

            if (diff > maxdif) //Can turn about 45 degrees 
            {
                diff = maxdif;
            }

            _logger.Log(LogLevel.Info,$"Returned Value: {diff}");

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