using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Autonoceptor.Shared.Gps;
using Autonoceptor.Shared.Gps.Enums;

namespace Autonoceptor.Shared.Utilities
{
    public static class GpsExtensions
    {
        private static double _lat;
        private static double _lon;
        private static GpsFixQuality _quality;
        private static double _heading;
        private static float _altitude;
        private static double _feetPerSecond;
        private static DateTime _dateTime;
        private static int _satellitesInView;
        private static int _signalToNoiseRatio;
        private static double _rtkAge;
        private static double _rtkRatio;
        private static double _hdop;

        private static bool _odometerCalibrated;

        private static bool _gyroAccelCalibrated;
        private static bool _sensorInputAvailable;
        private static double _odometerPulseCount;

        private static bool _movingBackward;
        private static double _gyroBias;
        private static double _odometerScalingFactor;
        private static double _rotationRate;
        private static double _distance;

        //.000001 should only record waypoint every 1.1132m or 3.65223097ft
        //The third decimal place is worth up to 110 m: it can identify a large agricultural field or institutional campus.
        //The fourth decimal place is worth up to 11 m: it can identify a parcel of land.It is comparable to the typical accuracy of an uncorrected GPS unit with no interference.
        //The fifth decimal place is worth up to 1.1 m: it distinguish trees from each other. Accuracy to this level with commercial GPS units can only be achieved with differential correction.            //Write GPS fix data to file, if switch is closed. Gps publishes fix data once a second
        //The sixth decimal place is worth up to 0.11 m: you can use this for laying out structures in detail, for designing landscapes, building roads. It should be more than good enough for tracking movements of glaciers and rivers. This can be achieved by taking painstaking measures with GPS, such as differentially corrected GPS.
        //The seventh decimal place is worth up to 11 mm: this is good for much surveying and is near the limit of what GPS-based techniques can achieve.
        //The eighth decimal place is worth up to 1.1 mm: this is good for charting motions of tectonic plates and movements of volcanoes. Permanent, corrected, constantly-running GPS base stations might be able to achieve this level of accuracy.
        //https://en.wikipedia.org/wiki/Decimal_degrees
        //http://navspark.mybigcommerce.com/content/S1722DR8_v0.4.pdf

        /// <summary>
        ///     Returns double[] [0] = distance to heading in inches. [1] = heading to destination waypoint
        /// </summary>
        /// <param name="currentLat"></param>
        /// <param name="currentLon"></param>
        /// <param name="destinationLat"></param>
        /// <param name="destinationLon"></param>
        /// <returns>distance to waypoint, and heading to waypoint</returns>
        public static double[] GetDistanceAndHeadingToDestination(double currentLat, double currentLon,
            double destinationLat, double destinationLon)
        {
            try
            {
                var diflat = (destinationLat - currentLat).ToRadians();

                currentLat = currentLat.ToRadians(); //convert current latitude to radians
                destinationLat = destinationLat.ToRadians(); //convert waypoint latitude to radians

                var diflon = (destinationLon - currentLon).ToRadians();
                //subtract and convert longitude to radians

                var distCalc = Math.Sin(diflat / 2.0) * Math.Sin(diflat / 2.0);
                var distCalc2 = Math.Cos(currentLat);

                distCalc2 = distCalc2 * Math.Cos(destinationLat);
                distCalc2 = distCalc2 * Math.Sin(diflon / 2.0);
                distCalc2 = distCalc2 * Math.Sin(diflon / 2.0); //and again, why?
                distCalc += distCalc2;
                distCalc = 2 * Math.Atan2(Math.Sqrt(distCalc), Math.Sqrt(1.0 - distCalc));
                distCalc = distCalc * 6371000.0;
                //Converting to meters. 6371000 is the magic number,  3959 is average Earth radius in miles
                distCalc = Math.Round(distCalc * 39.3701, 1); // and then to inches.

                currentLon = currentLon.ToRadians();
                destinationLon = destinationLon.ToRadians();

                var heading = Math.Atan2(Math.Sin(destinationLon - currentLon) * Math.Cos(destinationLat),
                    Math.Cos(currentLat) * Math.Sin(destinationLat) -
                    Math.Sin(currentLat) * Math.Cos(destinationLat) * Math.Cos(destinationLon - currentLon));

                heading = heading.ToDegrees();

                if (heading < 0)
                    heading += 360;

                return new[] { Math.Round(distCalc, 1), Math.Round(heading, 1) };
            }
            catch (Exception)
            {
                return new double[] { 0, 0 };
            }
        }

        public static double Latitude2Double(string lat, string ns)
        {
            if (lat.Length < 2 || string.IsNullOrEmpty(ns))
                return _lat;

            if (!double.TryParse(lat.Substring(2), out double med))
                return _lat;

            med = med / 60.0d;

            if (!double.TryParse(lat.Substring(0, 2), out double temp))
                return _lat;

            med += temp;

            if (ns.StartsWith("S"))
                med = -med;

            return Math.Round(med, 7); //gives accuracy of 1.1132m or 3.65223097ft
        }

        public static double Longitude2Double(this string lon, string we)
        {
            if (lon.Length < 2 || string.IsNullOrEmpty(we))
                return _lon;

            if (!double.TryParse(lon.Substring(3), out var med))
                return _lon;

            med = med / 60.0d;


            if (!double.TryParse(lon.Substring(0, 3), out var temp))
                return _lon;

            med += temp;

            if (we.StartsWith("W"))
                med = -med;

            return Math.Round(med, 7); //gives accuracy of 1.1132m or 3.65223097ft
        }

        public static GpsFixData ParseNmea(string data)
        {
            try
            {
                data = data.Replace("$", "");
                var tokens = data.Split(',');
                var type = tokens[0];

                switch (type)
                {
                    case "GNGGA": //Global Positioning System Fix Data
                        if (tokens.Length < 10)
                            return null;

                        var st = tokens[1];

                        _dateTime = (new DateTime(DateTime.Now.Year, DateTime.Now.Month, DateTime.Now.Day,
                            Convert.ToInt32(st.Substring(0, 2)), Convert.ToInt32(st.Substring(2, 2)),
                            Convert.ToInt32(st.Substring(4, 2)), DateTimeKind.Local)).AddHours(6).ToLocalTime();

                        _lat = Latitude2Double(tokens[2], tokens[3]);
                        _lon = Longitude2Double(tokens[4], tokens[5]);

                        if (int.TryParse(tokens[6], out var quality))
                            _quality = (GpsFixQuality)quality;

                        int.TryParse(tokens[7], out _satellitesInView);

                        if (float.TryParse(tokens[9], out _altitude))
                            _altitude = _altitude * 3.28084f;

                        double.TryParse(tokens[8], out _hdop);

                        break;
                    case "GNRMC": //Recommended minimum specific GPS/Transit data

                        if (tokens.Length < 9)
                            return null;

                        _lat = Latitude2Double(tokens[3], tokens[4]);
                        _lon = Longitude2Double(tokens[5], tokens[6]);

                        double fps = 0;
                        if (double.TryParse(tokens[7], out fps))
                            _feetPerSecond = Math.Round(fps * 1.68781, 2); //Convert knots to feet per second or "Speed over ground"

                        double dir = 0;
                        if (double.TryParse(tokens[8], out dir))
                            _heading = dir; //angle from true north that you are traveling or "Course made good"

                        break;
                    case "GNGSV": //Satellites in View

                        if (tokens.Length < 8)
                            return null;

                        if (int.TryParse(tokens[3], out var satellitesInView))
                            _satellitesInView = satellitesInView;

                        if (int.TryParse(tokens[7], out var signalToNoiseRatio))
                            _signalToNoiseRatio = signalToNoiseRatio;

                        break;
                    case "PSTI":
                        if (tokens.Length <= 1)
                            break;

                        if (tokens[1].Contains("30") && tokens.Length >= 16) //RTK
                        {
                            _lat = Latitude2Double(tokens[4], tokens[5]);
                            _lon = Longitude2Double(tokens[6], tokens[7]);

                            double.TryParse(tokens[14], out _rtkAge);

                            var t = tokens[15].Split('*')[0];
                            double.TryParse(t, out _rtkRatio);
                        }

                        if (tokens[1].Contains("20") && tokens.Length >= 12) //Dead reckoning
                        {
                            _odometerCalibrated = tokens[2].Equals("1");
                            _gyroAccelCalibrated = tokens[3].Equals("1");
                            _sensorInputAvailable = tokens[4].Equals("1");
                            _odometerPulseCount = Convert.ToDouble(tokens[5]);

                            switch (tokens[6])
                            {
                                case "A":
                                    _quality = GpsFixQuality.StandardGps;
                                    break;
                                case "N":
                                    _quality = GpsFixQuality.NoFix;
                                    break;
                                case "E": //Estimated, or Dead Reckoning
                                    _quality = GpsFixQuality.DeadReckoning;
                                    break;
                            }

                            _movingBackward = tokens[7].Equals("1");
                            _gyroBias = Convert.ToDouble(tokens[9]);
                            _odometerScalingFactor = Convert.ToDouble(tokens[10]);
                            _rotationRate = Convert.ToDouble(tokens[11]);
                            _distance = Convert.ToDouble(tokens[12].Split('*')[0]);
                        }

                        break;
                    default:
                        return null;
                }
            }
            catch
            {
                //No fix yet or malformed sentence
                return null;
            }

            var latLon = new GpsFixData
            {
                Lat = _lat,
                Lon = _lon,
                Altitude = _altitude,
                FeetPerSecond = _feetPerSecond,
                Quality = _quality.ToString(),
                SatellitesInView = _satellitesInView,
                SignalToNoiseRatio = _signalToNoiseRatio,
                Heading = _heading,
                DateTime = _dateTime,
                RtkAge = _rtkAge,
                RtkRatio = _rtkRatio,
                Hdop = _hdop,

                OdometerCalibrated = _odometerCalibrated,
                GyroAccelCalibrated = _gyroAccelCalibrated,
                SensorInputAvailable = _sensorInputAvailable,
                OdometerPulseCount = _odometerPulseCount,
                MovingBackward = _movingBackward,
                GyroBias = _gyroBias,
                OdometerScalingFactor = _odometerScalingFactor,
                RotationRate = _rotationRate,
                Distance = _distance
            };

            return latLon;
        }
    }
}