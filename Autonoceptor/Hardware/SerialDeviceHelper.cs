using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel.Contacts;
using Windows.Devices.Enumeration;
using Windows.Devices.SerialCommunication;

namespace Autonoceptor.Service.Hardware
{
    internal static class SerialDeviceHelper
    {
        internal static async Task<SerialDevice> GetSerialDeviceAsync(string identifier, int baudRate, TimeSpan readTimeout, TimeSpan writeTimeout)
        {
            var deviceInformationCollection = await DeviceInformation.FindAllAsync(SerialDevice.GetDeviceSelector());
            var selectedPort = deviceInformationCollection.LastOrDefault(d => d.Id.Contains(identifier) || d.Name.Contains(identifier));

            if (selectedPort == null)
            {
                return null;
            }

            var serialDevice = await SerialDevice.FromIdAsync(selectedPort.Id);

            if (serialDevice == null)
            {
                return null;
            }

            serialDevice.ReadTimeout = readTimeout;
            serialDevice.WriteTimeout = writeTimeout;
            serialDevice.BaudRate = (uint)baudRate;
            serialDevice.Parity = SerialParity.None;
            serialDevice.StopBits = SerialStopBitCount.One;
            serialDevice.DataBits = 8;
            serialDevice.Handshake = SerialHandshake.None;

            Debug.WriteLine($"Found - {identifier}");

            return serialDevice;
        }

        internal static async Task<List<string>> GetAvailablePorts()
        {
            return (from d in await DeviceInformation.FindAllAsync(SerialDevice.GetDeviceSelector()) select $"{d.Id} => {d.Name}").ToList();
        }
    }
}