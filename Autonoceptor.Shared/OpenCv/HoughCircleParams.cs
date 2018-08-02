namespace Autonoceptor.Shared.OpenCv
{
    public class HoughCircleParams
    {
        public float Dp { get; set; } = 1;
        public float MinDistance { get; set; } = 10;
        public int CannyThreshold { get; set; } = 150;
        public int VotesThreshold { get; set; } = 60;
        public int MinRadius { get; set; } = 2;
        public int MaxRadius { get; set; } = 140;
        public int MaxCircles { get; set; } = 8;
    }
}