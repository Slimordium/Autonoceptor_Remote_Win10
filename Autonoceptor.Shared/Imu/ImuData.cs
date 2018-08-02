using System;

namespace Autonoceptor.Shared.Imu
{
    public class ImuData 
    {
        public ImuData()
        {

        }

        public ImuData(string values)
        {
            if (values == null || values.Length < 6) return;




            Yaw = Convert.ToDouble(values[3]);
            Pitch = Convert.ToDouble(values[4]);
            Roll = Convert.ToDouble(values[5]);
        }

        public double Yaw { get; set; } = 0;
        public double Pitch { get; set; } = 0;
        public double Roll { get; set; } = 0;
    }
}