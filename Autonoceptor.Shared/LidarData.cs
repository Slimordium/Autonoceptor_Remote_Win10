namespace Autonoceptor.Shared
{
    public class LidarData
    {
        public ushort Distance { get; set; }
        public ushort Strength { get; set; }
        public ushort Reliability { get; set; }
        public double Angle { get; set; }
        public bool IsValid { get; set; }
    }

}