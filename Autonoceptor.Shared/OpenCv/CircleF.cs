namespace Autonoceptor.Shared.OpenCv
{
    //{"Center":{"IsEmpty":false,"X":585.0,"Y":301.0},"Radius":109.110039,"Area":37400.66360894806}

    public class CircleF
    {
        public PointF Center { get; set; }

        public float Radius { get; set; }

        public float Area { get; set; }
    }

    public class PointF
    {
        public bool IsEmpty { get; set; }

        public float X { get; set; }

        public float Y { get; set; }
    }
}