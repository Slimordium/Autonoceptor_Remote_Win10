using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.SerialCommunication;
using Windows.Storage.Streams;

namespace Autonoceptor.Service.Hardware
{
    public class ChannelData
    {
        public ushort ChannelId { get; set; }

        private int _analogValue;
        public int AnalogValue
        {
            get => _analogValue;
            set
            {
                DigitalValue = value > 1023;

                _analogValue = value;
            }
        }
        public bool DigitalValue { get; set; }
    }

    public class MaestroPwmController
    {
        private SerialDevice _maestroPwmDevice;

        private DataWriter _outputStream;
        private DataReader _inputStream;

        private CancellationToken _cancellationToken;

        private readonly SemaphoreSlim _readWriteSemaphore = new SemaphoreSlim(1,1);

        private Subject<ChannelData> _channelSubject;

        private readonly Dictionary<ushort, ChannelData> _channelValues = new Dictionary<ushort, ChannelData>();

        public MaestroPwmController(IEnumerable<ushort> inputChannels)
        {
            foreach (var c in inputChannels)
            {
                _channelValues.Add(c, new ChannelData {ChannelId = c});
            }
        }

        public async Task InitializeAsync(CancellationToken cancellationToken)
        {
            if (_maestroPwmDevice != null)
                return;

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

            await _readWriteSemaphore.WaitAsync();

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

            await _readWriteSemaphore.WaitAsync();

            _outputStream.WriteBytes(new[] { (byte)0xAA, (byte)0x0C, (byte)0x10, (byte)channel });//Forward / reverse
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

        public IObservable<ChannelData> GetObservable()
        {
            if (_channelSubject != null)
            {
                return _channelSubject.AsObservable();
            }

            _channelSubject = new Subject<ChannelData>();

            Task.Run(async () =>
            {
                while (!_cancellationToken.IsCancellationRequested)
                {
                    foreach (var channel in _channelValues)
                    {
                        var channelValue = await GetChannelValue(channel.Key);

                        var lastDigital = channel.Value.DigitalValue;

                        channel.Value.AnalogValue = channelValue;

                        if (channel.Value.DigitalValue != lastDigital)
                        {
                            _channelSubject.OnNext(channel.Value);
                        }

                        //await Task.Delay(100);
                    }
                }
            });

            return _channelSubject.AsObservable();
        }
    }
}