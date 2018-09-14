namespace Autonoceptor.Hardware
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

        public bool DigitalValue { get; private set; }
    }
}