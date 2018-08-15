using System;
// ReSharper disable InconsistentNaming

namespace Autonoceptor.Shared.Gps.Enums
{
    [Flags]
    public enum GpsFixQuality
    {
        NoFix,
        StandardGps,
        DiffGps,
        PPS,
        RTK,
        FloatRTK,
        DeadReckoning,
        Manual,
        Simulation
    }
}