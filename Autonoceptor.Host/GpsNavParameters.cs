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
            var diff = Math.Abs(GetCurrentHeading() - GetTargetHeading());

            try
            {

                //_logger.Log(LogLevel.Info, $"Distance to target: {_distanceToWaypoint}");
                //_logger.Log(LogLevel.Info, $"Diff: {diff}");

                var maxdif = 20 - 3 * Math.Atan((_distanceToWaypoint - 20) / 5);

                //_logger.Log(LogLevel.Info, $"Maximum difference{maxdif}");

                if (diff > maxdif) //Can turn about 45 degrees 
                {
                    diff = maxdif;
                }

                //_logger.Log(LogLevel.Info, $"Returned Value: {diff}");
            }
            catch (Exception e)
            {
                _logger.Log(LogLevel.Info, e.Message);
                if (diff > 30) { diff = 30; };
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