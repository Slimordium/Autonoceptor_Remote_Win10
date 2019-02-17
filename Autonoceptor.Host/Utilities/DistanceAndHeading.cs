using System;

namespace Autonoceptor.Shared.Utilities
{
    public class DistanceAndHeading
    {
        public double DistanceInInches { get; set; }
        public double DistanceInFeet => Math.Round(DistanceInInches / 12, 1);
        public double HeadingToWaypoint { get; set; }
        public bool IsValid { get; set; } = true;
    }
}