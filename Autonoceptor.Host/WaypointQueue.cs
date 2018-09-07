using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autonoceptor.Service.Hardware;
using Autonoceptor.Shared.Utilities;
using Newtonsoft.Json;
using Nito.AsyncEx;
using NLog;

namespace Autonoceptor.Host
{
    public class WaypointQueue : Queue<Waypoint>
    {
        private static readonly ILogger _logger = LogManager.GetCurrentClassLogger();
        
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

        //.000001 should only record waypoint every 1.1132m or 3.65223097ft
        //https://en.wikipedia.org/wiki/Decimal_degrees
        //http://navspark.mybigcommerce.com/content/S1722DR8_v0.4.pdf

        private readonly double _minWaypointDistance;
        private readonly SparkFunSerial16X2Lcd _lcd;

        ///  <summary>
        /// The third decimal place is worth up to 110 m: it can identify a large agricultural field or institutional campus.
        /// The fourth decimal place is worth up to 11 m: it can identify a parcel of land.It is comparable to the typical accuracy of an uncorrected GPS unit with no interference.
        /// The fifth decimal place is worth up to 1.1 m: it distinguish trees from each other. Accuracy to this level with commercial GPS units can only be achieved with differential correction.            //Write GPS fix data to file, if switch is closed. Gps publishes fix data once a second
        /// The sixth decimal place is worth up to 0.11 m: you can use this for laying out structures in detail, for designing landscapes, building roads. It should be more than good enough for tracking movements of glaciers and rivers. This can be achieved by taking painstaking measures with GPS, such as differentially corrected GPS.
        /// The seventh decimal place is worth up to 11 mm: this is good for much surveying and is near the limit of what GPS-based techniques can achieve.
        /// The eighth decimal place is worth up to 1.1 mm: this is good for charting motions of tectonic plates and movements of volcanoes. Permanent, corrected, constantly-running GPS base stations might be able to achieve this level of accuracy.
        ///  </summary>
        ///  <param name="minWaypointDistance"></param>
        /// <param name="lcd"></param>
        public WaypointQueue(double minWaypointDistance, SparkFunSerial16X2Lcd lcd) // = .0000001
        {
            _minWaypointDistance = minWaypointDistance;
            _lcd = lcd;
        }

        private DisplayGroup _displayGroup;

        public async Task InitializeAsync()
        {
            var displayGroup = new DisplayGroup
            {
                DisplayItems = new Dictionary<int, string> { { 1, "WP Q init" }, { 2, "Complete" } },
                GroupName = "WaypointQueue"
            };

            _displayGroup = await _lcd.AddDisplayGroup(displayGroup);
        }

        private async Task WriteToLcd(string line1, string line2, bool refreshDisplay = false)
        {
            _logger.Log(LogLevel.Info, $"{line1} / {line2}");

            _displayGroup.DisplayItems = new Dictionary<int, string>
            {
                {1, line1 },
                {2, line2 }
            };

            await _lcd.UpdateDisplayGroup(_displayGroup, refreshDisplay);
        }

        public new async Task Enqueue(Waypoint waypoint)
        {
            using (await _asyncLock.LockAsync())
            {
                if (!this.Any(fixData =>
                    Math.Abs(fixData.Lat - waypoint.Lat) < _minWaypointDistance ||
                    Math.Abs(fixData.Lon - waypoint.Lon) < _minWaypointDistance))
                {
                    await WriteToLcd($"({Count}) {waypoint.Lat}", $"{waypoint.Lon}");

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
                    await FileExtensions.SaveStringToFile(GetFileName(), JsonConvert.SerializeObject(this));

                    await WriteToLcd($"Saved {Count}", "waypoints...", true);

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
                    WaypointQueue waypoints = JsonConvert.DeserializeObject<WaypointQueue>(await GetFileName().ReadStringFromFile());

                    Clear(); //Remove everything from the queue

                    foreach (var wp in waypoints)
                    {
                        base.Enqueue(wp);
                    }

                    this.StartPoints = waypoints.StartPoints;

                    await WriteToLcd($"Waypoint Set {WaypointSetNumber}", "Load Successful", true);
                }
                catch (Exception e)
                {
                    await WriteToLcd($"Waypoint Set {WaypointSetNumber}", "Load Failed New Set", true);

                    Clear();
                    StartPoints = new List<Waypoint>();
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

                moveReq.HeadingToTargetWp = Math.Round(distanceAndHeading.HeadingToWaypoint);
                moveReq.DistanceToTargetWp = Math.Round(distanceAndHeading.DistanceInFeet);

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

                moveReq.HeadingToTargetWp = Math.Round(distanceAndHeading.HeadingToWaypoint);
                moveReq.DistanceToTargetWp = Math.Round(distanceAndHeading.DistanceInFeet);

                return moveReq;
            }
        }

        public async Task<double> GetWaypointSpeedSetting()
        {
            using (await _asyncLock.LockAsync())
            {
                return Peek().WaypointSpeed;
            }
        }

        private static Tuple<SteeringDirection, double> GetSteeringDirectionAndMagnitude(double currentHeading, double headingToWaypoint, double distanceInFt)
        {
            var steeringDirection = GetSteeringDirection(currentHeading, headingToWaypoint);
            var steeringMagnitude = GetSteeringMagnitude(currentHeading, headingToWaypoint, distanceInFt);

            return new Tuple<SteeringDirection, double>(steeringDirection, steeringMagnitude);
        }

        private static double GetSteeringMagnitude(double currentHeading, double targetHeading, double distanceToWaypoint)
        {
            var differenceInDegrees = Math.Abs(currentHeading - targetHeading);

            return differenceInDegrees;
        }

        private static SteeringDirection GetSteeringDirection(double currentHeading, double targetHeading)
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

        #region Waypoint Offsets

        // I will keep a list of the possible starting waypoints
        public List<Waypoint> StartPoints { get; set; } = new List<Waypoint>();
        public int TargetStartPoint { get; set; } = 0;

        public async Task AddStartingPoint(Waypoint newStartPoint)
        {
            StartPoints.Add(newStartPoint);

            await WriteToLcd($"New Startpoint", $"{StartPoints.Count - 1}", true);
        }

        public async Task IterateStartingPoint()
        {
            if (TargetStartPoint >= StartPoints.Count - 1)
            {
                TargetStartPoint = 0;
            }
            else
            {
                TargetStartPoint++;
            }

            await WriteToLcd($"Target Startpoint", TargetStartPoint.ToString(), true);
        }

        public async Task SetStartingPoint(Waypoint currentfix)
        {

            if (StartPoints.Count == 0)
            {
                await WriteToLcd($"No Startpoints Set", "No Offset Applied", true);
            }

            Waypoint.LonOffset = StartPoints[TargetStartPoint].Lon - currentfix.Lon;
            Waypoint.LatOffset = StartPoints[TargetStartPoint].Lat - currentfix.Lat;

            await WriteToLcd($"Set Startpoint", TargetStartPoint.ToString(), true);
        }

        #endregion

        #region Saving and Loading

        public int WaypointSetNumber { get; set; } = 0;

        public string GetFileName()
        {
            return $"Waypoints{WaypointSetNumber}.json";
        }
        
        public async Task IncreaseWaypointSetNumber()
        {
            WaypointSetNumber++;

            await Load();
        }

        public async Task DecreaseWaypointSetNumber()
        {
            WaypointSetNumber--;

            await Load();
        }



        #endregion
    }
}
