using System;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autonoceptor.Service.Hardware;
using Autonoceptor.Shared.Utilities;

namespace Autonoceptor.Host
{
    public class Car : Hardware
    {
        private IDisposable _steerMagnitudeDecayDisposable;

        private MoveRequest _moveRequest = new MoveRequest();

        private int _rightMax = 1861;
        private int _center = 1321;
        private int _leftMax = 837;

        private int _reverseMax = 1072;
        private int _stopped = 1471;
        private int _forwardMax = 1856; //1856

        private ushort _movementChannel = 0;
        private ushort _steeringChannel = 1;

        public Car()
        {
            _steerMagnitudeDecayDisposable = Observable.Interval(TimeSpan.FromMilliseconds(100)).Subscribe(async _ =>
            {
                if (Math.Abs(_moveRequest.SteeringMagnitude) < 5)
                    return;

                var moveRequest = Volatile.Read(ref _moveRequest);

                moveRequest.SteeringDirection = _moveRequest.SteeringDirection;
                moveRequest.SteeringMagnitude = _moveRequest.SteeringMagnitude * .6;

                await RequestMove(moveRequest);
            });
        }

        public async Task RequestMove(MoveRequest request)
        {
            _moveRequest = request;

            var moveValue = _stopped * 4;
            var steerValue = _center * 4;

            if (request.SteeringMagnitude > 45)
                request.SteeringMagnitude = 45;
          
            switch (request.SteeringDirection)
            {
                case SteeringDirection.Left:
                    steerValue = Convert.ToUInt16(request.SteeringMagnitude.Map(0, 45, _center, _leftMax)) * 4;
                    break;
                case SteeringDirection.Right:
                    steerValue = Convert.ToUInt16(request.SteeringMagnitude.Map(0, 45, _center, _rightMax)) * 4;
                    break;
            }

            await _maestroPwm.SetChannelValue(steerValue, _steeringChannel);

            //if (Volatile.Read(ref _followingWaypoints))
            //    return;

            switch (request.MovementDirection)
            {
                case MovementDirection.Forward:
                    moveValue = Convert.ToUInt16(request.MovementMagnitude.Map(0, 45, _stopped, _forwardMax)) * 4;
                    break;
                case MovementDirection.Reverse:
                    moveValue = Convert.ToUInt16(request.MovementMagnitude.Map(0, 45, _stopped, _reverseMax)) * 4;
                    break;
            }

            await _maestroPwm.SetChannelValue(moveValue, _movementChannel);
        }

        public async Task EmergencyStop(bool isCanceled = false)
        {
            if (isCanceled)
            {
                if (!Volatile.Read(ref _emergencyStopped))
                    return;

                Volatile.Write(ref _emergencyStopped, false);

                await _lcd.WriteAsync("E-Stop canceled", 2);

                return;
            }

            if (Volatile.Read(ref _emergencyStopped))
            {
                await _lcd.WriteAsync($"E-Stop @ {_lidarRange}cm", 2);
                return;
            }

            Volatile.Write(ref _emergencyStopped, true);

            await Stop();
        }
    }
}