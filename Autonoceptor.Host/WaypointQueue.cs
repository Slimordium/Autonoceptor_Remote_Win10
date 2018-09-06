using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autonoceptor.Shared.Utilities;
using Newtonsoft.Json;
using Nito.AsyncEx;
using NLog;

namespace Autonoceptor.Host
{
    public class WaypointQueue : Queue<Waypoint>
    {
        private static readonly ILogger _logger = LogManager.GetCurrentClassLogger();

        private readonly string _filename = $"waypoints.json";

        private readonly AsyncLock _asyncLock = new AsyncLock();

        private Waypoint _currentWaypoint;
        public Waypoint CurrentWaypoint
        {
            get
            {
                if (_currentWaypoint == null && this.Any())
                {
                    _currentWaypoint = Peek();
                }

                return _currentWaypoint;
            }
            set => _currentWaypoint = value;
        }

        private static double _steerMagModifier = 1.5;

        //.000001 should only record waypoint every 1.1132m or 3.65223097ft
        //https://en.wikipedia.org/wiki/Decimal_degrees
        //http://navspark.mybigcommerce.com/content/S1722DR8_v0.4.pdf

        private readonly double _minWaypointDistance;

        /// <summary>
        ///The third decimal place is worth up to 110 m: it can identify a large agricultural field or institutional campus.
        ///The fourth decimal place is worth up to 11 m: it can identify a parcel of land.It is comparable to the typical accuracy of an uncorrected GPS unit with no interference.
        ///The fifth decimal place is worth up to 1.1 m: it distinguish trees from each other. Accuracy to this level with commercial GPS units can only be achieved with differential correction.            //Write GPS fix data to file, if switch is closed. Gps publishes fix data once a second
        ///The sixth decimal place is worth up to 0.11 m: you can use this for laying out structures in detail, for designing landscapes, building roads. It should be more than good enough for tracking movements of glaciers and rivers. This can be achieved by taking painstaking measures with GPS, such as differentially corrected GPS.
        ///The seventh decimal place is worth up to 11 mm: this is good for much surveying and is near the limit of what GPS-based techniques can achieve.
        ///The eighth decimal place is worth up to 1.1 mm: this is good for charting motions of tectonic plates and movements of volcanoes. Permanent, corrected, constantly-running GPS base stations might be able to achieve this level of accuracy.
        /// </summary>
        /// <param name="minWaypointDistance"></param>
        public WaypointQueue(double minWaypointDistance = .0000001)
        {
            _minWaypointDistance = minWaypointDistance;
        }

        public new async Task Enqueue(Waypoint waypoint)
        {
            using (await _asyncLock.LockAsync())
            {
                if (!this.Any(fixData =>
                    Math.Abs(fixData.Lat - waypoint.Lat) < _minWaypointDistance ||
                    Math.Abs(fixData.Lon - waypoint.Lon) < _minWaypointDistance))
                {
                    base.Enqueue(waypoint);
                }
            }
        }

        public async Task<bool> Save()
        {
            using (await _asyncLock.LockAsync())
            {
                try
                {
                    await FileExtensions.SaveStringToFile(_filename, JsonConvert.SerializeObject(this));

                    _logger.Log(LogLevel.Info, $"Saved {Count} waypoints");

                    return true;
                }
                catch (Exception e)
                {
                    _logger.Log(LogLevel.Error, $"Could not save waypoints => {e.Message}");
                    return false;
                }
            }
        }

        /// <summary>
        /// This needs to be called before navigating
        /// </summary>
        /// <returns></returns>
        public async Task Load()
        {
            using (await _asyncLock.LockAsync())
            {
                try
                {
                    var waypoints = JsonConvert.DeserializeObject<Queue<Waypoint>>(await _filename.ReadStringFromFile());

                    Clear(); //Remove everything from the queue

                    _logger.Log(LogLevel.Info, $"Loaded {waypoints.Count} waypoints");

                    foreach (var wp in waypoints)
                    {
                        base.Enqueue(wp);
                    }
                }
                catch (Exception e)
                {
                    _logger.Log(LogLevel.Error, $"Could not load waypoints => {e.Message}");
                }
            }
        }

        public async Task<MoveRequest> GetMoveRequestForNextWaypoint(double yourLat, double yourLon, double currentHeading, double overrideCalculatedDistanceRemaining = 0)
        {
            using (await _asyncLock.LockAsync())
            {
                if (!this.Any())
                    return null;//Most likely we have already finished navigating

                var moveReq = new MoveRequest();
                
                var distanceAndHeading = GpsExtensions.GetDistanceAndHeadingToWaypoint(yourLat, yourLon, CurrentWaypoint.Lat, CurrentWaypoint.Lon);

                var radiusDistanceInCheck = 0d; //This is the GPS distance to WP, or the calculated distance from our current Lat/Lon

                Tuple<SteeringDirection, double> directionAndMagnitude;

                if (overrideCalculatedDistanceRemaining > 0)
                {
                    moveReq.Distance = overrideCalculatedDistanceRemaining;
                    radiusDistanceInCheck = overrideCalculatedDistanceRemaining;
                    directionAndMagnitude = GetSteeringDirectionAndMagnitude(currentHeading, distanceAndHeading.HeadingToWaypoint, overrideCalculatedDistanceRemaining);
                }
                else
                {
                    moveReq.Distance = distanceAndHeading.DistanceInInches;
                    radiusDistanceInCheck = distanceAndHeading.DistanceInInches;
                    directionAndMagnitude = GetSteeringDirectionAndMagnitude(currentHeading, distanceAndHeading.HeadingToWaypoint, distanceAndHeading.DistanceInInches);
                }
                
                moveReq.SteeringDirection = directionAndMagnitude.Item1;
                moveReq.SteeringMagnitude = directionAndMagnitude.Item2;

                if (radiusDistanceInCheck <= CurrentWaypoint.Radius)
                {
                    Dequeue(); //Remove waypoint from queue, as we have arrived, move on to next one if available

                    if (!this.Any())
                        return null; //At last waypoint

                    CurrentWaypoint = Peek();
                }
                else
                {
                    return moveReq;
                }

                //Don't override distance, since this is the next one, and the override distance does not apply

                moveReq = new MoveRequest();

                distanceAndHeading = GpsExtensions.GetDistanceAndHeadingToWaypoint(yourLat, yourLon, CurrentWaypoint.Lat, CurrentWaypoint.Lon);

                moveReq.Distance = distanceAndHeading.DistanceInInches;
                directionAndMagnitude = GetSteeringDirectionAndMagnitude(currentHeading, distanceAndHeading.HeadingToWaypoint, distanceAndHeading.DistanceInInches);

                moveReq.SteeringDirection = directionAndMagnitude.Item1;
                moveReq.SteeringMagnitude = directionAndMagnitude.Item2;

                return moveReq;
            }
        }

        private static Tuple<SteeringDirection, double> GetSteeringDirectionAndMagnitude(double currentHeading, double headingToWaypoint, double distanceInFt)
        {
            var steeringDirection = GetSteeringDirection(currentHeading, headingToWaypoint);
            var steeringMagnitude = GetSteeringMagnitude(currentHeading, headingToWaypoint, distanceInFt);

            //var steeringMagnitude = GetSteeringMagnitudeAlternate(currentHeading, headingToWaypoint, distanceInFt);

            return new Tuple<SteeringDirection, double>(steeringDirection, steeringMagnitude);
        }

        public void SetSteerMagnitudeModifier(double mod)
        {
            Volatile.Write(ref _steerMagModifier, mod);
        }

        public static double GetSteeringMagnitude(double currentHeading, double targetHeading, double distanceToWaypoint)
        {
            var differenceInDegrees = Math.Abs(currentHeading - targetHeading);// / Volatile.Read(ref _steerMagModifier);

            try
            {
                //var maxAllowedMagnitude = 100 - 3 * Math.Atan((distanceToWaypoint - 20) / 5);

                //if (differenceInDegrees > maxAllowedMagnitude)
                //{
                //    differenceInDegrees = maxAllowedMagnitude;
                //}
            }
            catch (Exception e)
            {
                _logger.Log(LogLevel.Info, e.Message);

                differenceInDegrees = 0;
            }

            return differenceInDegrees;
        }

        public static double GetSteeringMagnitudeAlternate(double currentHeading, double targetHeading, double distanceToWaypoint)
        {
            var differenceInDegrees = Math.Abs(currentHeading - targetHeading);

            //Cap the distance we care about to 180in / 15ft
            if (distanceToWaypoint > 180)
                distanceToWaypoint = 180;

            var divider = distanceToWaypoint * .01; //180 = 1.8

            var newDifferenceInDegrees = differenceInDegrees / divider;


            return newDifferenceInDegrees;
        }

        public static SteeringDirection GetSteeringDirection(double currentHeading, double targetHeading)
        {
            SteeringDirection steerDirection;

            var angleDifferenceInDegrees = currentHeading - targetHeading;

            if (angleDifferenceInDegrees < 0)
            {
                steerDirection = Math.Abs(angleDifferenceInDegrees) > 180 ? SteeringDirection.Left : SteeringDirection.Right;
            }
            else
            {
                steerDirection = Math.Abs(angleDifferenceInDegrees) > 180 ? SteeringDirection.Right : SteeringDirection.Left;
            }

            return steerDirection;
        }
    }
}
