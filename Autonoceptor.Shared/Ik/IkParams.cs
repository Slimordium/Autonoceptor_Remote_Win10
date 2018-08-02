
using Autonoceptor.Shared.Ik.Enums;

namespace Autonoceptor.Shared.Ik
{
    public class IkParams
    {
        public bool Override { get; set; }

        public IkParams(bool zero = false)
        {
            if (!zero) return;

            Override = true;

            BodyPositionY = 0;
            GaitSpeedMs = 0;
            LegLiftHeight = 0;
            GaitType = GaitType.None;
        }

        public static IkParams operator +(IkParams b, IkParams c)
        {
            return new IkParams
            {
                LegLiftHeight = b.LegLiftHeight + c.LegLiftHeight,
                BodyPositionY = b.BodyPositionY + c.BodyPositionY,
                GaitSpeedMs = b.GaitSpeedMs + c.GaitSpeedMs,
                LengthZ = b.LengthZ + c.LengthZ,
                LengthX = b.LengthX + c.LengthX,
                RotationY = b.RotationY + c.RotationY,
                BodyRotationX = b.BodyRotationX + c.BodyRotationX,
                BodyRotationY = b.BodyRotationY + c.BodyRotationY,
                BodyRotationZ = b.BodyRotationZ + c.BodyRotationZ
            };
        }

        public static IkParams operator -(IkParams b, IkParams c)
        {
            return new IkParams
            {
                LegLiftHeight = b.LegLiftHeight - c.LegLiftHeight,
                BodyPositionY = b.BodyPositionY - c.BodyPositionY,
                GaitSpeedMs = b.GaitSpeedMs - c.GaitSpeedMs,
                LengthZ = b.LengthZ - c.LengthZ,
                LengthX = b.LengthX - c.LengthX,
                RotationY = b.RotationY - c.RotationY,
                BodyRotationX = b.BodyRotationX - c.BodyRotationX,
                BodyRotationY = b.BodyRotationY - c.BodyRotationY,
                BodyRotationZ = b.BodyRotationZ - c.BodyRotationZ
            };
        }

        public GaitType GaitType { get; set; } = GaitType.TripleTripod16;
        public double BodyRotationX { get; set; } = 0;
        public double BodyRotationY { get; set; } = 0;
        public double BodyRotationZ { get; set; } = 0;
        public double BodyPositionX { get; set; } = 0;

        /// <summary>
        /// Vertical, 0 being the floor. A value of 42, would be 42MM from the floor
        /// </summary>
        public double BodyPositionY { get; set; } = 70;

        /// <summary>
        /// Forward/back body offset
        /// </summary>
        public double BodyPositionZ { get; set; } = 0;

        /// <summary>
        /// Turn left/right
        /// </summary>
        public double RotationY { get; set; } = 0;

        /// <summary>
        /// Travel length forward/backward
        /// </summary>
        public double LengthZ { get; set; } = 0;

        /// <summary>
        /// Travel length side to side
        /// </summary>
        public double LengthX { get; set; } = 0;

        /// <summary>
        /// How many miliseconds should each movement command take
        /// </summary>
        public double GaitSpeedMs { get; set; } = 15;

        /// <summary>
        /// If this is true, a move request is sent. When this is false, all servos are turned off
        /// </summary>
        public bool MovementEnabled { get; set; } = false;

        /// <summary>
        /// How many millimeters should the feet come off of the ground
        /// </summary>
        public double LegLiftHeight { get; set; } = 70;

    }
}