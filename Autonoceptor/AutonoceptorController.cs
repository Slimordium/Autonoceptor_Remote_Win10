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
        private SerialDevice _maestroPwmDevice;

        private DataWriter _maestroOutputStream;

        private CancellationToken _cancellationToken;

        public async Task InitializeAsync(CancellationToken cancellationToken)
        {
            _cancellationToken = cancellationToken;

            _maestroPwmDevice = await SerialDeviceHelper.GetSerialDeviceAsync("142361d3&0&0000", 9600, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

            if (cancellationToken.IsCancellationRequested)
                return;

            _maestroPwmDevice.DataBits = 7;

            _maestroOutputStream = new DataWriter(_maestroPwmDevice.OutputStream);

            _cancellationToken = cancellationToken;
            _cancellationToken.Register(async () =>
            {
                var outputVal = 1500;

                var lsb = Convert.ToByte(outputVal & 0x7f);
                var msb = Convert.ToByte((outputVal >> 7) & 0x7f);

                _maestroOutputStream.WriteBytes(new[] { (byte)0x84, (byte)0x00, lsb, msb });//Stop
                await _maestroOutputStream.StoreAsync();
            });
        }

        public async Task OnNextXboxData(XboxData xboxData)
        {
            if (_cancellationToken.IsCancellationRequested || _maestroOutputStream == null)
                return;

            var direction = 5932;

            switch (xboxData.RightStick.Direction)
            {
                case Direction.UpLeft:
                case Direction.DownLeft:
                case Direction.Left:
                    direction = Convert.ToUInt16(xboxData.RightStick.Magnitude.Map(0, 10000, 1483, 1060)) * 4;
                    break;
                case Direction.UpRight:
                case Direction.DownRight:
                case Direction.Right:
                    direction = Convert.ToUInt16(xboxData.RightStick.Magnitude.Map(0, 10000, 1483, 1900)) * 4;
                    break;
            }

            var lsb = Convert.ToByte((direction & 0x7f));
            var msb = Convert.ToByte((direction >> 7) & 0x7f);
          
            _maestroOutputStream.WriteBytes(new[] { (byte)0x84, (byte)0x01, lsb, msb });//Steering 
            await _maestroOutputStream.StoreAsync();

            var forwardMagnitude = Convert.ToUInt16(xboxData.LeftTrigger.Map(0, 33000, 1500, 1090)) * 4;
            var reverseMagnitude = Convert.ToUInt16(xboxData.RightTrigger.Map(0, 33000, 1500, 1800)) * 4;

            var outputVal = forwardMagnitude;

            if (reverseMagnitude > 6000)
            {
                outputVal = reverseMagnitude;
            }

            lsb = Convert.ToByte(outputVal & 0x7f);
            msb = Convert.ToByte((outputVal >> 7) & 0x7f);

            _maestroOutputStream.WriteBytes(new[] { (byte)0x84, (byte)0x00, lsb, msb });//Forward / reverse
            await _maestroOutputStream.StoreAsync();
        }
    }
}