using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autonoceptor.Shared.Gps;
using Autonoceptor.Shared.Utilities;
using Newtonsoft.Json;

namespace Autonoceptor.Host
{
    public class WaypointList : List<GpsFixData>
    {
        private readonly string _filename = $"waypointdata.json";

        //.000001 should only record waypoint every 1.1132m or 3.65223097ft
        //The third decimal place is worth up to 110 m: it can identify a large agricultural field or institutional campus.
        //The fourth decimal place is worth up to 11 m: it can identify a parcel of land.It is comparable to the typical accuracy of an uncorrected GPS unit with no interference.
        //The fifth decimal place is worth up to 1.1 m: it distinguish trees from each other. Accuracy to this level with commercial GPS units can only be achieved with differential correction.            //Write GPS fix data to file, if switch is closed. _gps publishes fix data once a second
        //The sixth decimal place is worth up to 0.11 m: you can use this for laying out structures in detail, for designing landscapes, building roads. It should be more than good enough for tracking movements of glaciers and rivers. This can be achieved by taking painstaking measures with GPS, such as differentially corrected GPS.
        //The seventh decimal place is worth up to 11 mm: this is good for much surveying and is near the limit of what GPS-based techniques can achieve.
        //The eighth decimal place is worth up to 1.1 mm: this is good for charting motions of tectonic plates and movements of volcanoes. Permanent, corrected, constantly-running GPS base stations might be able to achieve this level of accuracy.
        //https://en.wikipedia.org/wiki/Decimal_degrees
        //http://navspark.mybigcommerce.com/content/S1722DR8_v0.4.pdf

        public new void Add(GpsFixData gpsFixData)
        {
            if (!this.Any(fixData => Math.Abs(fixData.Lat - gpsFixData.Lat) < .000001 || Math.Abs(fixData.Lon - gpsFixData.Lon) < .000001))
            {
                base.Add(gpsFixData);
            }
        }

        public async Task<bool> Save()
        {
            try
            {
                await FileExtensions.SaveStringToFile(_filename, JsonConvert.SerializeObject(this));
                return true;
            }
            catch (Exception)
            {
                Console.WriteLine("I had a problem saving the waypoints. ");
                return false;
            }
        }

        public async Task<WaypointList> Load()
        {
            try
            {
                var newlist = JsonConvert.DeserializeObject<WaypointList>(await _filename.ReadStringFromFile());
                return newlist;
            }
            catch (Exception)
            {
                Console.WriteLine("Could not load the waypoint list. ");
                return new WaypointList();
                // Also tasty
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="index"></param>
        /// <returns></returns>
        public double GetInchesToNextWaypoint(int index)
        {
            if (Count <= index + 1)
                return 0;

            var vals = GpsExtensions.GetDistanceAndHeadingToDestination(this[index].Lat, this[index].Lon, this[index + 1].Lat, this[index + 1].Lon);

            return vals[0];
        }

        public double GetHeadingToNextWaypoint(int index)
        {
            if (Count <= index + 1)
                return 0;

            var vals = GpsExtensions.GetDistanceAndHeadingToDestination(this[index].Lat, this[index].Lon, this[index + 1].Lat, this[index + 1].Lon);

            return vals[1];
        }
    }
}
