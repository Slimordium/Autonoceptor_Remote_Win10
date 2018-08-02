using System;

namespace Autonoceptor.Shared.Mpu
{
    public class MpuSensorEventArgs : EventArgs
    {
        public byte Status { get; set; }
        public double SamplePeriod { get; set; }
        public MpuSensorValue[] Values { get; set; }
    }
}