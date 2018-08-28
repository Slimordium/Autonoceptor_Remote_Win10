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
    }
}