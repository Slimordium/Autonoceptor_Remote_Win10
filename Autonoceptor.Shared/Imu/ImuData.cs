using System;
using System.Threading;

namespace Autonoceptor.Shared.Imu
{
    public class ImuData
    {
        private static double _yawCorrection;

        public static double YawCorrection
        {
            get => Volatile.Read(ref _yawCorrection);
            set => Volatile.Write(ref _yawCorrection, value);
        }

        private double _yaw;

        public double Yaw
        {
            get => Math.Abs(_yaw - YawCorrection);
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