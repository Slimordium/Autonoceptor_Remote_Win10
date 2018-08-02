using System;
using System.Drawing;

namespace Autonoceptor.Shared.OpenCv
{
    public struct LineSegment2D
    {
        private Point _p1;
        private Point _p2;

        public Point P1
        {
            get
            {
                return this._p1;
            }
            set
            {
                this._p1 = value;
            }
        }

        public Point P2
        {
            get
            {
                return this._p2;
            }
            set
            {
                this._p2 = value;
            }
        }

        public LineSegment2D(Point p1, Point p2)
        {
            this._p1 = p1;
            this._p2 = p2;
        }

        //public PointF Direction
        //{
        //    get
        //    {
        //        Point point1 = this.P1;
        //        int x1 = point1.X;
        //        point1 = this.P2;
        //        int x2 = point1.X;
        //        int num1 = x1 - x2;
        //        Point point2 = this.P1;
        //        int y1 = point2.Y;
        //        point2 = this.P2;
        //        int y2 = point2.Y;
        //        int num2 = y1 - y2;
        //        float num3 = (float)Math.Sqrt((double)(num1 * num1 + num2 * num2));
        //        return new PointF((float)num1 / num3, (float)num2 / num3);
        //    }
        //}

        public int Side(Point point)
        {
            Point point1 = this.P2;
            int x1 = point1.X;
            point1 = this.P1;
            int x2 = point1.X;
            int num1 = x1 - x2;
            int y1 = point.Y;
            point1 = this.P1;
            int y2 = point1.Y;
            int num2 = y1 - y2;
            int num3 = num1 * num2;
            int x3 = point.X;
            point1 = this.P1;
            int x4 = point1.X;
            int num4 = x3 - x4;
            point1 = this.P2;
            int y3 = point1.Y;
            point1 = this.P1;
            int y4 = point1.Y;
            int num5 = y3 - y4;
            int num6 = num4 * num5;
            float num7 = (float)(num3 - num6);
            return (double)num7 > 0.0 ? 1 : ((double)num7 < 0.0 ? -1 : 0);
        }

        //public double GetExteriorAngleDegree(LineSegment2D otherLine)
        //{
        //    PointF direction1 = this.Direction;
        //    PointF direction2 = otherLine.Direction;
        //    double num = (Math.Atan2((double)direction2.Y, (double)direction2.X) - Math.Atan2((double)direction1.Y, (double)direction1.X)) * (180.0 / Math.PI);
        //    return num <= -180.0 ? num + 360.0 : (num > 180.0 ? num - 360.0 : num);
        //}

        public double Length
        {
            get
            {
                Point point1 = this.P1;
                int x1 = point1.X;
                point1 = this.P2;
                int x2 = point1.X;
                int num1 = x1 - x2;
                Point point2 = this.P1;
                int y1 = point2.Y;
                point2 = this.P2;
                int y2 = point2.Y;
                int num2 = y1 - y2;
                return Math.Sqrt((double)(num1 * num1 + num2 * num2));
            }
        }
    }
}