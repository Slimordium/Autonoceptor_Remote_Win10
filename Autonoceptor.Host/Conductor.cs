using System;
using System.Threading;
using System.Threading.Tasks;
using Autonoceptor.Service.Hardware;
using Autonoceptor.Shared.Utilities;
using Hardware.Xbox;
using Hardware.Xbox.Enums;

namespace Autonoceptor.Host
{
    public class Conductor
    {
        private readonly MaestroPwmController _maestroPwmController = new MaestroPwmController();
        private CancellationToken _cancellationToken;
        private bool _emergencyStopped;

        private int _rightMax = 1900;
        private int _center = 1500;
        private int _leftMax = 1060;

        private int _forwardMax = 1090;
        private int _stopped = 1500;
        private int _reverseMax = 1800;

        private ushort _movementChannel = 0;
        private ushort _steeringChannel = 1;

        public async Task InitializeAsync(CancellationTokenSource cancellationTokenSource)
        {
            _cancellationToken = cancellationTokenSource.Token;

            await _maestroPwmController.InitializeAsync(_cancellationToken);

            cancellationTokenSource.Token.Register(async () =>
            {
                await Stop();
            });
        }

        private async Task Stop()
        {
            await Task.WhenAll(new[]
            {
                _maestroPwmController.SetChannelValue(_stopped, _movementChannel),
                _maestroPwmController.SetChannelValue(_center, _steeringChannel)
            });
        }


        /// <summary>
        /// Valid for channels 12 - 23
        /// </summary>
        /// <param name="channel"></param>
        /// <returns></returns>
        public async Task<bool> GetDigitalChannelState(ushort channel)
        {
            if (_maestroPwmController == null)
                return false;

            var value = await _maestroPwmController.GetChannelValue(channel);

            return value != 0;
        }

        /// <summary>
        /// Valid for channels 0 - 12
        /// </summary>
        /// <param name="channel"></param>
        /// <returns></returns>
        public async Task<int> GetAnalogChannelState(ushort channel)
        {
            if (_maestroPwmController == null)
                return 0;

            var value = await _maestroPwmController.GetChannelValue(channel);

            return value;
        }

        public async Task EmergencyStop(bool isCanceled = false)
        {
            if (isCanceled)
            {
                _emergencyStopped = false;

                return;
            }

            _emergencyStopped = true;

            await Stop();
        }

        public async Task MoveRequest(MoveRequest request)
        {
            if (_maestroPwmController == null || _cancellationToken.IsCancellationRequested || _emergencyStopped)
                return;

            var moveValue = _stopped;
            var steerValue = _center;

            switch (request.MovementDirection)
            {
                case MovementDirection.Forward:
                    moveValue = Convert.ToUInt16(request.MovementMagnitude.Map(0, 100, _stopped, _forwardMax)) * 4;
                    break;
                case MovementDirection.Reverse:
                    moveValue = Convert.ToUInt16(request.MovementMagnitude.Map(0, 100, _stopped, _reverseMax)) * 4;
                    break;
            }

            switch (request.SteeringDirection)
            {
                case SteeringDirection.Left:
                    steerValue = Convert.ToUInt16(request.SteeringMagnitude.Map(0, 100, _center, _leftMax)) * 4;
                    break;
                case SteeringDirection.Right:
                    steerValue = Convert.ToUInt16(request.SteeringMagnitude.Map(0, 100, _center, _rightMax)) * 4;
                    break;
            }

            await Task.WhenAll(new[]
            {
                _maestroPwmController.SetChannelValue(moveValue, _movementChannel),
                _maestroPwmController.SetChannelValue(steerValue, _steeringChannel)
            });
        }

        public async Task OnNextXboxData(XboxData xboxData)
        {
            if (_maestroPwmController == null || _cancellationToken.IsCancellationRequested || _emergencyStopped)
                return;

            var direction = 5932;

            switch (xboxData.RightStick.Direction)
            {
                case Direction.UpLeft:
                case Direction.DownLeft:
                case Direction.Left:
                    direction = Convert.ToUInt16(xboxData.RightStick.Magnitude.Map(0, 10000, _center, _leftMax)) * 4;
                    break;
                case Direction.UpRight:
                case Direction.DownRight:
                case Direction.Right:
                    direction = Convert.ToUInt16(xboxData.RightStick.Magnitude.Map(0, 10000, _center, _rightMax)) * 4;
                    break;
            }

            await _maestroPwmController.SetChannelValue(direction, _steeringChannel); //Channel 1 is Steering

            var forwardMagnitude = Convert.ToUInt16(xboxData.LeftTrigger.Map(0, 33000, _stopped, _forwardMax)) * 4;
            var reverseMagnitude = Convert.ToUInt16(xboxData.RightTrigger.Map(0, 33000, _stopped, _reverseMax)) * 4;

            var outputVal = forwardMagnitude;

            if (reverseMagnitude > 6000)
            {
                outputVal = reverseMagnitude;
            }

            await _maestroPwmController.SetChannelValue(outputVal, _movementChannel); //Channel 0 is the motor driver
        }
    }
}