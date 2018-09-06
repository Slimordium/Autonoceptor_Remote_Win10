using System.Collections.Generic;
using System.Threading;
using Autonoceptor.Shared;
using Autonoceptor.Shared.Gps;
using Autonoceptor.Shared.Imu;

namespace Autonoceptor.Host
{
    public class Waypoint
    {
        public double DistanceToWaypoint { get; set; }

        public double Lat { get; set; }
        public double Lon { get; set; }

        public WaypointType Behaviour { get; set; } = WaypointType.Continue;

        /// <summary>
        /// Radius in inches that is acceptable to come within the waypoint.
        /// This will have to be based on the GPS update rate. Our GPS updates at 1Hz, we travel about 2-2.5 feet per second.
        /// If all our "turns" were perfect, we could get away with a "breadcrumb" style of waypoints.
        /// Currently it takes 2-3 corrections to hit a waypoint. Which means the start nav should be at least 7.5ft away
        /// However, there is a delay between getting a GPS updated, and when we turn, which means it adds about 1-1.5 of decision making
        /// time per update, or in short, you will need to be 12+ feet away. 
        /// </summary>
        public int Radius { get; set; } = 50;

        public override string ToString()
        {
            return $"{Lat}, {Lon}, {Radius}, {Behaviour}";
        }
    }

    public enum WaypointType { Continue, Stop, Pause }
}