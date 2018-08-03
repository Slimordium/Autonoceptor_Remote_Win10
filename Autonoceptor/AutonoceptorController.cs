using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.SerialCommunication;
using Windows.Storage.Streams;
using Autonoceptor.Service.Hardware;
using Autonoceptor.Shared.Utilities;
using Hardware.Xbox;
using Hardware.Xbox.Enums;

namespace Autonoceptor.Service
{
    public class AutonoceptorController
    {
        private SerialDevice _steeringSerialDevice;
        private SerialDevice _motorSerialDevice;

        private DataWriter _steeringOutputStream;
        private DataWriter _motorOutputStream;

        private bool _started = false;
        private CancellationToken _cancellationToken;

        public async Task InitializeAsync(CancellationToken cancellationToken)
        {
            _steeringSerialDevice = await SerialDeviceHelper.GetSerialDeviceAsync("4236", 9600, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

            _motorSerialDevice = await SerialDeviceHelper.GetSerialDeviceAsync("111a", 9600, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

            while (_steeringSerialDevice == null || _motorSerialDevice == null)
            {
                await Task.Delay(500);
            }

            _steeringOutputStream = new DataWriter(_steeringSerialDevice.OutputStream);

            _motorOutputStream = new DataWriter(_motorSerialDevice.OutputStream);

            _cancellationToken = cancellationToken;
            _cancellationToken.Register(async () =>
            {
                _motorOutputStream.WriteBytes(Encoding.ASCII.GetBytes("X\r"));
                await _motorOutputStream.StoreAsync();
            });
        }

        public async Task OnNextXboxData(XboxData xboxData)
        {
            if (_cancellationToken.IsCancellationRequested)
                return;

            decimal direction = 127;

            switch (xboxData.RightStick.Direction)
            {
                case Direction.UpLeft:
                case Direction.DownLeft:
                case Direction.Left:
                    direction = Math.Round((decimal) Convert.ToUInt16(xboxData.RightStick.Magnitude.Map(0, 10000, 127, 0)));
                    break;
                case Direction.UpRight:
                case Direction.DownRight:
                case Direction.Right:
                    direction = Math.Round((decimal) Convert.ToUInt16(xboxData.RightStick.Magnitude.Map(0, 10000, 128, 254)));
                    break;
            }

            if (_steeringOutputStream != null)
            {
                _steeringOutputStream.WriteBytes(new[] { (byte)0xFF, (byte)0x00, (byte)direction });
                await _steeringOutputStream.StoreAsync();
            }

            var reverseMagnitude = Math.Round(xboxData.LeftTrigger.Map(0, 33000, 0, 40));
            var forwardMagnitude = Math.Round(xboxData.RightTrigger.Map(0, 33000, 0, 60));

            var moveString = "X\r"; //Stop if not going forward or backwards

            if (xboxData.FunctionButtons.Contains(FunctionButton.Start))
            {
                //_started = !_started;
            }
            else
            {
                if (reverseMagnitude > forwardMagnitude)
                {
                    moveString = $"R{reverseMagnitude}%\r";
                }
                else
                {
                    moveString = $"F{forwardMagnitude}%\r";
                }
            }
            //if (_started)
            //{
                
            //}

            if (_motorOutputStream != null)
            {
                _motorOutputStream.WriteBytes(Encoding.ASCII.GetBytes(moveString));
                await _motorOutputStream.StoreAsync();
            }
        }
    }
}