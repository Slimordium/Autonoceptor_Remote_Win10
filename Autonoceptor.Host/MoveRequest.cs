namespace Autonoceptor.Host
{
    public class MoveRequest
    {
        public MoveRequest()
        {
        }

        public MoveRequest(MoveRequest moveRequest)
        {
            MovementDirection = moveRequest.MovementDirection;
            MovementMagnitude = moveRequest.MovementMagnitude;

            SteeringMagnitude = moveRequest.SteeringMagnitude;
            SteeringDirection = moveRequest.SteeringDirection;
        }

        /// <summary>
        /// 100 being 100% throttle, 0 being stopped or 0% throttle
        /// </summary>
        public double MovementMagnitude { get; set; }

        /// <summary>
        /// Max turn angle is 100, 0 is not turned
        /// </summary>
        public double SteeringMagnitude { get; set; }

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