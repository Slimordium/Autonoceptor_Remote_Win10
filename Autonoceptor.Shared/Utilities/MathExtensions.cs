using System;

namespace Autonoceptor.Shared.Utilities
{
    public static class MathExtensions
    {
        public static double Map(this double valueToMap, double valueToMapMin, double valueToMapMax, double outMin, double outMax)
        {
            return (valueToMap - valueToMapMin)*(outMax - outMin)/(valueToMapMax - valueToMapMin) + outMin;
        }

        public static double Map(this int valueToMap, double valueToMapMin, double valueToMapMax, double outMin, double outMax)
        {
            return (valueToMap - valueToMapMin) * (outMax - outMin) / (valueToMapMax - valueToMapMin) + outMin;
        }

        public static double ToRadians(this double angle)
        {
            return Math.PI * angle / 180;
        }

        public static double ToDegrees(this double angle)
        {
            return angle * (180.0 / Math.PI);
        }
    }
}