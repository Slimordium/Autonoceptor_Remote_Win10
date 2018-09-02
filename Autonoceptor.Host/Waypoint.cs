using System.Collections.Generic;
using Autonoceptor.Shared;
using Autonoceptor.Shared.Gps;
using Autonoceptor.Shared.Imu;

namespace Autonoceptor.Host
{
    public class Waypoint
    {
        public GpsFixData GpsFixData { get; set; } = new GpsFixData();

        public ImuData ImuData { get; set; } = new ImuData();

        public List<LidarData> LidarDatas { get; set; } = new List<LidarData>();

        public WaypointType Behaviour { get; set; } = WaypointType.Continue;

        /// <summary>
        /// Radius in inches that is acceptable to come within the waypoint. 
        /// </summary>
        public int Radius { get; set; } = 40;
    }

    public enum WaypointType { Continue, Stop, Pause }
}