using System;
using System.Threading;

namespace Autonoceptor.Shared.Imu
{
    public class ImuData
    {
        
        public double Yaw { get; set; }

        public double UncorrectedYaw { get; set; }

        public double Pitch { get; set; } = 0;
        public double Roll { get; set; } = 0;
    }
}