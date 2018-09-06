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
    public class WaypointList 
    {
        private readonly ILogger _logger = LogManager.GetCurrentClassLogger();

        private readonly string _filename = $"waypoints.json";

        private List<Waypoint> _waypoints = new List<Waypoint>();
        private List<Waypoint> _preStartWaypoints = new List<Waypoint>();
        private readonly AsyncLock _asyncLock = new AsyncLock();

        //.000001 should only record waypoint every 1.1132m or 3.65223097ft
        //The third decimal place is worth up to 110 m: it can identify a large agricultural field or institutional campus.
        //The fourth decimal place is worth up to 11 m: it can identify a parcel of land.It is comparable to the typical accuracy of an uncorrected GPS unit with no interference.
        //The fifth decimal place is worth up to 1.1 m: it distinguish trees from each other. Accuracy to this level with commercial GPS units can only be achieved with differential correction.            //Write GPS fix data to file, if switch is closed. Gps publishes fix data once a second
        //The sixth decimal place is worth up to 0.11 m: you can use this for laying out structures in detail, for designing landscapes, building roads. It should be more than good enough for tracking movements of glaciers and rivers. This can be achieved by taking painstaking measures with GPS, such as differentially corrected GPS.
        //The seventh decimal place is worth up to 11 mm: this is good for much surveying and is near the limit of what GPS-based techniques can achieve.
        //The eighth decimal place is worth up to 1.1 mm: this is good for charting motions of tectonic plates and movements of volcanoes. Permanent, corrected, constantly-running GPS base stations might be able to achieve this level of accuracy.
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
        public WaypointList(double minWaypointDistance = .0000001)
        {
            _minWaypointDistance = minWaypointDistance;
        }

        public int Count => _waypoints.Count;

        public List<Waypoint> ActiveWaypoints => _waypoints;

        public async Task AddFirst(Waypoint waypoint)
        {
            using (var l = await _asyncLock.LockAsync())
            {
                if (!_waypoints.Any(fixData =>
                    Math.Abs(fixData.Lat - waypoint.Lat) < _minWaypointDistance ||
                    Math.Abs(fixData.Lon - waypoint.Lon) < _minWaypointDistance))
                {
                    _waypoints.Insert(0, waypoint);
                    _preStartWaypoints.Insert(0, waypoint);
                }
            }
        }

        public async Task AddLast(Waypoint waypoint)
        {
            using (var l = await _asyncLock.LockAsync())
            {
                if (!_waypoints.Any(fixData =>
                    Math.Abs(fixData.Lat - waypoint.Lat) < _minWaypointDistance ||
                    Math.Abs(fixData.Lon - waypoint.Lon) < _minWaypointDistance))
                {
                    _waypoints.Add(waypoint);
                    _preStartWaypoints.Add(waypoint);
                }
            }
        }

        public async Task RemoveWaypoint(Waypoint waypoint)
        {
            using (var l = await _asyncLock.LockAsync())
            {
                var waypoints = new List<Waypoint>(_waypoints);
                waypoints.Remove(waypoint);

                _waypoints = new List<Waypoint>(waypoints);
            }
        }

        public async Task ClearWaypoints()
        {
            using (var l = await _asyncLock.LockAsync())
            {
                _waypoints = new List<Waypoint>();
                _preStartWaypoints = new List<Waypoint>();
            }
        }

        public async Task ResetActiveWaypoints()
        {
            using (var l = await _asyncLock.LockAsync())
            {
                _waypoints = new List<Waypoint>(_preStartWaypoints);
            }
        }

        public async Task<bool> Save()
        {
            using (var l = await _asyncLock.LockAsync())
            {
                try
                {
                    await FileExtensions.SaveStringToFile(_filename, JsonConvert.SerializeObject(_waypoints));
                    return true;
                }
                catch (Exception e)
                {
                    _logger.Log(LogLevel.Error, $"Could not save waypoints => {e.Message}");
                    return false;
                }
            }
        }

        public async Task Load()
        {
            using (var l = await _asyncLock.LockAsync())
            {
                try
                {
                    _waypoints = JsonConvert.DeserializeObject<List<Waypoint>>(await _filename.ReadStringFromFile());
                }
                catch (Exception e)
                {
                    _logger.Log(LogLevel.Error, $"Could not load waypoints => {e.Message}");
                }
            }
        }

        public async Task<MoveRequest> GetMoveRequestForNextWaypoint(double yourLat, double yourLon, double currentHeading)
        {
            using (var l = await _asyncLock.LockAsync())
            {
                var nextWp = _waypoints.FirstOrDefault();

                var moveReq = new MoveRequest();

                if (nextWp == null)
                {
                    return null;
                }

                var dh = GpsExtensions.GetDistanceAndHeadingToWaypoint(yourLat, yourLon, nextWp.Lat, nextWp.Lon);

                var directionAndMagnitude = GetSteeringDirectionAndMagnitude(currentHeading, dh.HeadingToWaypoint, dh.DistanceInFeet);

                moveReq.Distance = dh.DistanceInInches;
                moveReq.SteeringDirection = directionAndMagnitude.Item1;
                moveReq.SteeringMagnitude = directionAndMagnitude.Item2;

                if (dh.DistanceInInches <= nextWp.Radius)
                {
                    _waypoints.RemoveAt(0);
                }
                else
                {
                    return moveReq;
                }

                nextWp = _waypoints.FirstOrDefault();

                if (nextWp == null)
                    return null;

                dh = GpsExtensions.GetDistanceAndHeadingToWaypoint(yourLat, yourLon, nextWp.Lat, nextWp.Lon);

                directionAndMagnitude = GetSteeringDirectionAndMagnitude(currentHeading, dh.HeadingToWaypoint, dh.DistanceInFeet);

                moveReq.Distance = dh.DistanceInInches;
                moveReq.SteeringDirection = directionAndMagnitude.Item1;
                moveReq.SteeringMagnitude = directionAndMagnitude.Item2;

                return moveReq;
            }
        }

        private Tuple<SteeringDirection, double> GetSteeringDirectionAndMagnitude(double currentHeading, double headingToWaypoint, double distanceInFt)
        {
            var steeringDirection = GetSteeringDirection(currentHeading, headingToWaypoint);
            var steeringMagnitude = GetSteeringMagnitude(currentHeading, headingToWaypoint, distanceInFt);

            return new Tuple<SteeringDirection, double>(steeringDirection, steeringMagnitude);
        }

        private double _steerMagModifier = 1.5;

        public void SetSteerMagnitudeModifier(double mod)
        {
            Volatile.Write(ref _steerMagModifier, mod);
        }

        public double GetSteeringMagnitude(double currentHeading, double targetHeading, double distanceToWaypoint)
        {
            var diff = Math.Abs(currentHeading - targetHeading) / Volatile.Read(ref _steerMagModifier);

            try
            {
                var maxdif = 100 - 3 * Math.Atan((distanceToWaypoint - 20) / 5);

                if (diff > maxdif)
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

        public SteeringDirection GetSteeringDirection(double currentHeading, double targetHeading)
        {
            SteeringDirection steerDirection;

            var diff = currentHeading - targetHeading;

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
