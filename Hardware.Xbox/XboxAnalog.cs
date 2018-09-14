using Hardware.Xbox.Enums;

namespace Hardware.Xbox
{

    public class XboxAnalog
    {
        public Direction Direction { get; set; } = Direction.None;

        public double Magnitude;
    }

}