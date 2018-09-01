using System;
using Autonoceptor.Shared.Gps.Enums;

namespace Autonoceptor.Shared.Gps
{
    public class GpsFixData
    {
        public double Lat { get; set; } = 0;
        public double Lon { get; set; } = 0;
        public string Quality { get; set; } = GpsFixQuality.NoFix.ToString();
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
            return $"Lat: {Lat}, Lon: {Lon}, Heading: {Heading}, Quality: {Quality}, HDOP: {Hdop}, Sats: {SatellitesInView}";
        }
    }
}