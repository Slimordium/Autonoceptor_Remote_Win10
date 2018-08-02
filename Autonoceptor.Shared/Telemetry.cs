using Autonoceptor.Shared.Imu;

namespace Autonoceptor.Shared
{
    public class Telemetry
    {
        public ImuData ImuData { get; set; }

        public double LeftRange { get; set; }

        public double RightRange { get; set; }
    }
}