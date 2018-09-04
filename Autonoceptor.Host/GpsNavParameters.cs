using System;
using System.Threading;
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

        public double GetDistanceToWaypoint()
        {
            return Volatile.Read(ref _distanceToWaypoint);
        }

        public void SetDistanceToWaypoint(double distanceToTarget)
        {
            Volatile.Write(ref _distanceToWaypoint, distanceToTarget / 12);
        }

        public double GetTargetPpi()
        {
            return Volatile.Read(ref _currentPpi);
        }

        public void SetTargetPpi(double ppi)
        {
            Volatile.Write(ref _currentPpi, ppi);
        }

        public double GetLastMoveMagnitude()
        {
            return Volatile.Read(ref _lastMoveMagnitude);
        }

        public void SetLastMoveMagnitude(double mag)
        {
            Volatile.Write(ref _lastMoveMagnitude, mag);
        }

        public double GetTargetHeading()
        {
            return Volatile.Read(ref _targetHeading);
        }

        public void SetTargetHeading(double heading)
        {
            Volatile.Write(ref _targetHeading, heading);
        }

        public double GetCurrentHeading()
        {
            return Volatile.Read(ref _currentHeading);
        }

        public void SetCurrentHeading(double heading)
        {
            Volatile.Write(ref _currentHeading, heading);
        }

        public double GetSteeringMagnitude()
        {
            var diff = Math.Abs(GetCurrentHeading() - GetTargetHeading()) / 1.5;

            try
            {
                var maxdif = 80 - 3 * Math.Atan((_distanceToWaypoint - 20) / 5);

                if (diff > maxdif) //Can turn about 45 degrees 
                {
                    diff = maxdif;
                }
            }
            catch (Exception e)
            {
                _logger.Log(LogLevel.Info, e.Message);

                diff = 0;
            }
            
            return diff;
        }

        public SteeringDirection GetSteeringDirection()
        {
            SteeringDirection steerDirection;

            var diff = GetCurrentHeading() - GetTargetHeading();

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