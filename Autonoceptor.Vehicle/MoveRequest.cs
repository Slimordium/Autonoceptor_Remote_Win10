namespace Autonoceptor.Vehicle
{
    public class MoveRequest
    {
        public double HeadingToTargetWp { get; set; }

        public double DistanceInToTargetWp { get; set; }

        /// <summary>
        /// Max turn angle is 100, 0 is not turned
        /// </summary>
        public double SteeringMagnitude { get; set; }

        public SteeringDirection SteeringDirection { get; set; } = SteeringDirection.Center;
    }

    public enum SteeringDirection
    {
        Left,
        Center,
        Right
    }

    public enum MovementDirection
    {
        Forward,
        Stopped,
        Reverse
    }
}