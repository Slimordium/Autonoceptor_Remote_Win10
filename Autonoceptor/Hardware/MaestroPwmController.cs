using System;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.SerialCommunication;
using Windows.Storage.Streams;

namespace Autonoceptor.Service.Hardware
{
    public class MaestroPwmController
    {
        private SerialDevice _maestroPwmDevice;

        private DataWriter _outputStream;
        private DataReader _inputStream;

        private CancellationToken _cancellationToken;

        private readonly SemaphoreSlim _readWriteSemaphore = new SemaphoreSlim(1,1); 

        public async Task InitializeAsync(CancellationToken cancellationToken)
        {
            _cancellationToken = cancellationToken;

            _maestroPwmDevice = await SerialDeviceHelper.GetSerialDeviceAsync("142361d3&0&0000", 9600, TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(30));

            if (cancellationToken.IsCancellationRequested)
                return;

            _maestroPwmDevice.DataBits = 7;

            _outputStream = new DataWriter(_maestroPwmDevice.OutputStream);
            _inputStream = new DataReader(_maestroPwmDevice.InputStream);
        }

       public async Task SetChannelValue(int value, ushort channel)
        {
            if (_outputStream == null)
                throw new InvalidOperationException("Output stream is null?");

            await _readWriteSemaphore.WaitAsync(_cancellationToken);

            var lsb = Convert.ToByte(value & 0x7f);
            var msb = Convert.ToByte((value >> 7) & 0x7f);

            _outputStream.WriteBytes(new[] { (byte)0x84, (byte)channel, lsb, msb });
            await _outputStream.StoreAsync();

            _readWriteSemaphore.Release(1);
        }

        public async Task<int> GetChannelValue(ushort channel)
        {
            if (_outputStream == null || _inputStream == null)
                throw new InvalidOperationException("Output or Input stream is null?");

            await _readWriteSemaphore.WaitAsync(_cancellationToken);

            _outputStream.WriteBytes(new[] { (byte)0xAA, (byte)0x00, (byte)0x10, (byte)channel });//Forward / reverse
            await _outputStream.StoreAsync();

            await _inputStream.LoadAsync(2);
            var inputBytes = new byte[2];
            _inputStream.ReadBytes(inputBytes);

            _readWriteSemaphore.Release(1);

            return await Task.FromResult(BitConverter.ToUInt16(inputBytes, 0) * 4);
        }

        //Get Position
        //Compact protocol: 0x90, channel number
        //Pololu protocol: 0xAA, device number, 0x10, channel number
        //Response: position low 8 bits, position high 8 bits
        //This command allows the device communicating with the Maestro to get the position value of a
        //channel.The position is sent as a two-byte response immediately after the command is received.

        //If the specified channel is configured as a servo, this position value represents the current pulse width
        //    that the Maestro is transmitting on the channel, reflecting the effects of any previous commands,
        //    speed and acceleration limits, or scripts running on the Maestro.
        //    If the channel is configured as a digital output, a position value less than 6000 means the Maestro
        //is driving the line low, while a position value of 6000 or greater means the Maestro is driving the line
        //    high.
        //    If the channel is configured as an input, the position represents the voltage measured on the channel.
        //    The inputs on channels 0–11 are analog: their values range from 0 to 1023, representing voltages from
        //0 to 5 V.The inputs on channels 12–23 are digital: their values are either exactly 0 or exactly 1023.
        //Note that the formatting of the position in this command differs from the target/speed/acceleration
        //    formatting in the other commands.Since there is no restriction on the high bit, the position is formatted
        //as a standard little-endian two-byte unsigned integer.For example, a position of 2567 corresponds to
        //a response 0x07, 0x0A.

        /// <summary>
        /// Pins 12-23 are digital
        /// </summary>
        /// <param name="channelNumber"></param>
        /// <param name="updateInterval"></param>
        /// <returns></returns>
        public IObservable<bool> GetDigitalChannelObservable(ushort channelNumber, TimeSpan updateInterval)
        {
            if (channelNumber < 12 || channelNumber > 23)
                throw new InvalidOperationException("Valid Channels are 12 - 23");

            var dataObservable = new Subject<bool>();

            Task.Run(async () =>
            {
                var lastChannelValue = 0;

                while (!_cancellationToken.IsCancellationRequested)
                {
                    var channelValue = await GetChannelValue(channelNumber);

                    if (channelValue == 0 && channelValue != lastChannelValue)
                    {
                        dataObservable.OnNext(false);
                    }

                    if (channelValue == 1023 && channelValue != lastChannelValue)
                    {
                        dataObservable.OnNext(true);
                    }

                    lastChannelValue = channelValue;

                    await Task.Delay(updateInterval, _cancellationToken);
                }
            });

            return dataObservable.AsObservable();
        }

        /// <summary>
        /// Pins 0 - 11 are analog
        /// </summary>
        /// <param name="channelNumber"></param>
        /// <param name="updateInterval"></param>
        /// <param name="triggerAfter"> Current value must deviate by this ammount before a value will be returned</param>
        /// <returns></returns>
        public IObservable<int> GetAnalogChannelObservable(ushort channelNumber, TimeSpan updateInterval, ushort triggerAfter = 0)
        {
            if (channelNumber > 11)
                throw new InvalidOperationException("Valid Channels are 0 - 11");

            var dataObservable = new Subject<int>();

            Task.Run(async () =>
            {
                var lastChannelValue = 0;

                while (!_cancellationToken.IsCancellationRequested)
                {
                    var channelValue = await GetChannelValue(channelNumber);

                    if (channelValue > lastChannelValue && channelValue > lastChannelValue + triggerAfter)
                    {
                        dataObservable.OnNext(channelValue);
                    }

                    if (channelValue < lastChannelValue && channelValue < lastChannelValue - triggerAfter)
                    {
                        dataObservable.OnNext(channelValue);
                    }

                    lastChannelValue = channelValue;

                    await Task.Delay(updateInterval, _cancellationToken);
                }
            });

            return dataObservable.AsObservable();
        }
    }
}