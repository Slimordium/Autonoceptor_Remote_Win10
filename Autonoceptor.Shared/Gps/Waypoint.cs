﻿
namespace Autonoceptor.Shared.Gps
{
    public class Waypoint
    {
        public double DistanceToWaypoint { get; set; }

        public double Lat { get => _lat + LatOffset; set => _lat = value; }
        public double Lon { get => _lon + LonOffset; set => _lon = value; }

        public static double LatOffset { get; set; } = 0;
        public static double LonOffset { get; set; } = 0;

        private double _lat;
        private double _lon;

        public double WaypointSpeed { get; set; } = 10;

        public WaypointType Behaviour { get; set; } = WaypointType.Continue;

        /// <summary>
        /// Radius in inches that is acceptable to come within the waypoint.
        /// This will have to be based on the GPS update rate. Our GPS updates at 1Hz, we travel about 2-2.5 feet per second.
        /// If all our "turns" were perfect, we could get away with a "breadcrumb" style of waypoints.
        /// Currently it takes 2-3 corrections to hit a waypoint. Which means the start nav should be at least 7.5ft away
        /// However, there is a delay between getting a GPS updated, and when we turn, which means it adds about 1-1.5 of decision making
        /// time per update, or in short, you will need to be 12+ feet away. 
        /// </summary>
        public int Radius { get; set; } = 30;

        public override string ToString()
        {
            return $"{Lat}, {Lon}, {Radius}, {Behaviour}";
        }
    }

}