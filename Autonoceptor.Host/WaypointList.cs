using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using NLog;

namespace Autonoceptor.Host
{
    public class WaypointList : List<Waypoint>
    {
        private readonly ILogger _logger = LogManager.GetCurrentClassLogger();

        private readonly string _filename = $"waypoints.json";

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
        public WaypointList(double minWaypointDistance = .000001)
        {
            _minWaypointDistance = minWaypointDistance;
        }

        public new void Add(Waypoint waypoint)
        {
            if (!this.Any(fixData => Math.Abs(fixData.GpsFixData.Lat - waypoint.GpsFixData.Lat) < _minWaypointDistance || Math.Abs(fixData.GpsFixData.Lon - waypoint.GpsFixData.Lon) < _minWaypointDistance))
            {
                base.Add(waypoint);
            }
        }

        public async Task<bool> Save()
        {
            try
            {
                await FileExtensions.SaveStringToFile(_filename, JsonConvert.SerializeObject(this));
                return true;
            }
            catch (Exception e)
            {
                _logger.Log(LogLevel.Error, $"Could not save waypoints => {e.Message}");
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
            catch (Exception e)
            {
                _logger.Log(LogLevel.Error, $"Could not load waypoints => {e.Message}");

                return new WaypointList();
            }
        }


        //TODO: Broken

        //public double GetInchesToNextWaypoint(int index)
        //{
        //    if (Count <= index + 1)
        //        return 0;

        //    var vals = GpsExtensions.GetDistanceAndHeadingToDestination(this[index].Lat, this[index].Lon, this[index + 1].Lat, this[index + 1].Lon);

        //    return vals[0];
        //}

        //public double GetHeadingToNextWaypoint(int index)
        //{
        //    if (Count <= index + 1)
        //        return 0;

        //    var vals = GpsExtensions.GetDistanceAndHeadingToDestination(this[index].Lat, this[index].Lon, this[index + 1].Lat, this[index + 1].Lon);

        //    return vals[1];
        //}
    }
}
