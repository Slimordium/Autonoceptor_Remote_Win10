using System;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Devices.HumanInterfaceDevice;
using Windows.Storage;
using Hardware.Xbox.Enums;

namespace Hardware.Xbox
{
    public class XboxDevice
    {
        private double _deadzoneTolerance = 5000; //Was 1000

        private HidDevice _deviceHandle;

        private readonly Subject<XboxData> _subject = new Subject<XboxData>();

        public async Task<bool> InitializeAsync(CancellationToken cancellationToken)
        {
            //USB\VID_045E&PID_0719\E02F1950 - receiver
            //USB\VID_045E & PID_02A1 & IG_00\6 & F079888 & 0 & 00  - XboxIkController
            //0x01, 0x05 = game controllers

            var deviceInformationCollection = await DeviceInformation.FindAllAsync(HidDevice.GetDeviceSelector(0x01, 0x05));

            if (!deviceInformationCollection.Any())
                return false;

            var isConnected = await ConnectToController(deviceInformationCollection);

            return await Task.FromResult(isConnected);
        }

        public IObservable<XboxData> GetObservable()
        {
            return _subject.AsObservable();
        }

        private async Task<bool> ConnectToController(DeviceInformationCollection deviceInformationCollection)
        {
            foreach (var d in deviceInformationCollection)
            {
                _deviceHandle = await HidDevice.FromIdAsync(d.Id, FileAccessMode.Read);

                if (_deviceHandle == null)
                    continue;

                _deviceHandle.InputReportReceived += InputReportReceived;
            }

            if (_deviceHandle == null)
                return await Task.FromResult(false);

            return await Task.FromResult(true);
        }

        private void InputReportReceived(HidDevice hidDevice, HidInputReportReceivedEventArgs args)
        {
            var dPad = args.Report.GetNumericControl(0x01, 0x39).Value;

            var lstickX = args.Report.GetNumericControl(0x01, 0x30).Value - 32768d;
            var lstickY = args.Report.GetNumericControl(0x01, 0x31).Value - 32768d;

            var rstickX = args.Report.GetNumericControl(0x01, 0x33).Value - 32768d;
            var rstickY = args.Report.GetNumericControl(0x01, 0x34).Value - 32768d;

            var lt = Math.Max(0, args.Report.GetNumericControl(0x01, 0x32).Value - 32768d);
            var rt = Math.Max(0, -1 * (args.Report.GetNumericControl(0x01, 0x32).Value - 32768d));

            var xboxEvent = new XboxData
            {
                LeftStick = new XboxAnalog
                {
                    Direction = CoordinatesToDirection(lstickX, lstickY),
                    Magnitude = GetMagnitude(lstickX, lstickY)
                },
                RightStick = new XboxAnalog
                {
                    Direction = CoordinatesToDirection(rstickX, rstickY),
                    Magnitude = GetMagnitude(rstickX, rstickY)
                },
                LeftTrigger = lt,
                RightTrigger = rt,
                //Dpad = ,
                FunctionButtons = args.Report.ActivatedBooleanControls.Select(btn => (int)(btn.Id - 5)).Select(id => (FunctionButton)id)
            };

            _subject.OnNext(xboxEvent);
        }

        /// <summary>
        ///     Gets the magnitude of the vector formed by the X/Y coordinates
        /// </summary>
        /// <param name="x">Horizontal coordinate</param>
        /// <param name="y">Vertical coordinate</param>
        /// <returns>True if the coordinates are inside the dead zone</returns>
        private double GetMagnitude(double x, double y)
        {
            var magnitude = Math.Sqrt(Math.Pow(x, 2) + Math.Pow(y, 2));

            if (magnitude < _deadzoneTolerance)
                magnitude = 0;
            else
            {
                // Scale so deadzone is removed, and max value is 10000
                magnitude = (magnitude - _deadzoneTolerance) / (32768 - _deadzoneTolerance) * 10000;

                if (magnitude > 10000)
                    magnitude = 10000;
            }

            return magnitude;
        }

        /// <summary>
        ///     Converts thumbstick X/Y coordinates centered at (0,0) to a direction
        /// </summary>
        /// <param name="x">Horizontal coordinate</param>
        /// <param name="y">Vertical coordinate</param>
        /// <returns>Action that the coordinates resolve to</returns>
        private static Direction CoordinatesToDirection(double x, double y)
        {
            var radians = Math.Atan2(y, x);
            var orientation = radians * (180 / Math.PI);

            orientation = orientation
                          + 180 // adjust so values are 0-360 rather than -180 to 180
                          + 22.5 // offset so the middle of each direction has a +/- 22.5 buffer
                          + 270; // adjust so when dividing by 45, up is 1

            orientation = orientation % 360;

            // Dividing by 45 should chop the orientation into 8 chunks, which 
            // maps 0 to Up.  Shift that by 1 since we need 1-8.
            var direction = (int)(orientation / 45) + 1;

            return (Direction)direction;
        }
    }
}