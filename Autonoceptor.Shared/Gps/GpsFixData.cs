using System;
using System.Diagnostics;
using Autonoceptor.Shared.Gps.Enums;

namespace Autonoceptor.Shared.Gps
{
    public class GpsFixData
    {
        public GpsFixData() { }

        public GpsFixData(string rawData)
        {
            var aParsed = rawData.Split(',');

            if (aParsed.Length < 5)
            {
                Debug.WriteLine($"Could not parse waypoint data - {rawData}");
                return;
            }

            DateTime = Convert.ToDateTime(aParsed[0]);
            Lat = double.Parse(aParsed[1]);
            Lon = double.Parse(aParsed[2]);
            Heading = double.Parse(aParsed[3]);
            FeetPerSecond = double.Parse(aParsed[4]);
            Quality = (GpsFixQuality)Enum.Parse(typeof(GpsFixQuality), aParsed[5]);
        }

        public double Lat { get; set; } = 0;
        public double Lon { get; set; } = 0;
        public GpsFixQuality Quality { get; set; } = GpsFixQuality.NoFix;
        public double Heading { get; set; } = 0;
        public float Altitude { get; set; } = 0;
        public double FeetPerSecond { get; set; } = 0;
        public DateTime DateTime { get; set; } = DateTime.MinValue;
        public int SatellitesInView { get; set; } = 0;
        public int SignalToNoiseRatio { get; set; } = 0;
        public double RtkAge { get; set; } = 0;
        public double RtkRatio { get; set; } = 0;
        public double Hdop { get; set; } = 0;
        public double EastProjectionOfBaseLine { get; set; } = 0;
        public double NorthProjectionOfBaseLine { get; set; } = 0;
        public double UpProjectionOfBaseLine { get; set; } = 0;
        public bool OdometerCalibrated { get; set; }
        public bool GyroAccelCalibrated { get; set; }
        public bool SensorInputAvailable { get; set; }
        public double OdometerPulseCount { get; set; }
        public bool MovingBackward { get; set; }
        public double GyroBias { get; set; }
        public double OdometerScalingFactor { get; set; }
        public double RotationRate { get; set; }
        public double Distance { get; set; }

        public override string ToString()
        {
            return $"{DateTime},{Lat},{Lon},{Heading},{FeetPerSecond},{Quality},{SatellitesInView},{SignalToNoiseRatio},{RtkAge},{RtkRatio}{'\n'}";
        }
    }
}