namespace Autonoceptor.Host
{
    public class MoveRequest
    {
        private MoveRequestType _moveRequestType;

        public MoveRequest(MoveRequestType requestType)
        {
            _moveRequestType = requestType;
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

        private void ScaleGpsSteering(double steeringMagnitude)
        {

        }

        private void ScaleGpsMovement(double movementMagnitude)
        {

        }

        private void ScaleXboxSteering(double steeringMagnitude)
        {

        }

        private void ScaleXboxMovement(double movementMagnitude)
        {

        }
    }

    public enum MoveRequestType
    {
        Gps,
        Lidar,
        Xbox,
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