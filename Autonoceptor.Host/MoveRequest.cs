namespace Autonoceptor.Host
{
    public class MoveRequest
    {
        /// <summary>
        /// 100 being 100% throttle, 0 being stopped or 0% throttle
        /// </summary>
        public int MovementMagnitude { get; set; }

        /// <summary>
        /// Max turn angle is 100, 0 is not turned
        /// </summary>
        public int SteeringMagnitude { get; set; }

        public SteeringDirection SteeringDirection { get; set; } = SteeringDirection.Center;

        public MovementDirection MovementDirection { get; set; } = MovementDirection.Forward;

        public double Distance { get; set; }
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