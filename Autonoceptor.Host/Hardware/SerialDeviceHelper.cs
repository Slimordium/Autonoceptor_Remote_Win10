using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Devices.SerialCommunication;
using NLog;

namespace Autonoceptor.Host.Hardware
{
    public static class SerialDeviceHelper
    {
        private static readonly ILogger _logger = LogManager.GetCurrentClassLogger();

        public static async Task<SerialDevice> GetSerialDeviceAsync(string identifier, int baudRate, TimeSpan readTimeout, TimeSpan writeTimeout)
        {
            var deviceInformationCollection = await DeviceInformation.FindAllAsync(SerialDevice.GetDeviceSelector());
            var selectedPort = deviceInformationCollection.LastOrDefault(d => d.Id.Contains(identifier) || d.Name.Contains(identifier));

            if (selectedPort == null)
            {
                _logger.Log(LogLevel.Error, $"selectedPort '{identifier}' not found");
                return null;
            }

            var serialDevice = await SerialDevice.FromIdAsync(selectedPort.Id);

            if (serialDevice == null)
            {
                _logger.Log(LogLevel.Error, $"serialDevice '{identifier}' not found");
                return null;
            }

            serialDevice.ReadTimeout = readTimeout;
            serialDevice.WriteTimeout = writeTimeout;
            serialDevice.BaudRate = (uint)baudRate;
            serialDevice.Parity = SerialParity.None;
            serialDevice.StopBits = SerialStopBitCount.One;
            serialDevice.DataBits = 8;
            serialDevice.Handshake = SerialHandshake.None;

            _logger.Log(LogLevel.Info, $"Found and opened serialDevice '{identifier}'");

            return serialDevice;
        }

        public static async Task<List<string>> GetAvailablePorts()
        {
            return (from d in await DeviceInformation.FindAllAsync(SerialDevice.GetDeviceSelector()) select $"{d.Id} => {d.Name}").ToList();
        }
    }
}