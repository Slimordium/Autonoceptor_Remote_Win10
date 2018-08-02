using System.Collections.Generic;
using Hardware.Xbox.Enums;

namespace Hardware.Xbox
{
    public class XboxData
    {
        public XboxAnalog RightStick { get; set; }

        public XboxAnalog LeftStick { get; set; }

        public double RightTrigger { get; set; }

        public double LeftTrigger { get; set; }

        public Direction Dpad { get; set; }

        public IEnumerable<FunctionButton> FunctionButtons { get; set; }
    }

    public class XboxAnalog
    {
        public Direction Direction { get; set; } = Direction.None;

        public double Magnitude;
    }

}