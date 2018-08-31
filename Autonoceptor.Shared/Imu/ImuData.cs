using System;

namespace Autonoceptor.Shared.Imu
{
    public class ImuData 
    {
        public static double YawCorrection { get; set; }

        private double _yaw;

        public double Yaw
        {
            get => Math.Abs(_yaw + YawCorrection);
            set
            {
                _yaw = value;
                UncorrectedYaw = value;
            } 
        }

        public double UncorrectedYaw { get; private set; }

        public double Pitch { get; set; } = 0;
        public double Roll { get; set; } = 0;
    }
}