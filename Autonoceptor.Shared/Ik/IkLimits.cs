
using Autonoceptor.Shared.Ik.Enums;

namespace Autonoceptor.Shared.IK{
    public class IkLimits
    {
        public IkLimits()
        {
            SetGaitDefaults(GaitType.TripleTripod16);
        }

        public int GaitSpeedMax { get; set; }
        public int GaitSpeedMin { get; set; }
        public int LegLiftHeightUpperLimit { get; set; }
        public int LegLiftHeightLowerLimit { get; set; }
        public int TravelLengthZupperLimit { get; set; }
        public int TravelLengthZlowerLimit { get; set; }
        public int TravelLengthXlimit { get; set; }
        public int TravelRotationYlimit { get; set; }

        public void SetGaitDefaults(GaitType gaitType)
        {
            switch (gaitType)
            {
                case GaitType.Tripod8:
                    //BodyPositionY = 80;
                    //LegLiftHeight = 60;
                    //GaitSpeedMs = 44;

                    GaitSpeedMax = 500;
                    GaitSpeedMin = 30;
                    LegLiftHeightUpperLimit = 65;
                    LegLiftHeightLowerLimit = 30;
                    TravelLengthZupperLimit = 170;
                    TravelLengthZlowerLimit = 80;
                    TravelLengthXlimit = 25;
                    TravelRotationYlimit = 25;
                    break;
                case GaitType.TripleTripod12:
                    //BodyPositionY = 75;
                    //LegLiftHeight = 60;
                    //GaitSpeedMs = 25;

                    GaitSpeedMax = 500;
                    GaitSpeedMin = 25;
                    TravelLengthZupperLimit = 170;
                    TravelLengthZlowerLimit = 80;
                    TravelLengthXlimit = 25;
                    LegLiftHeightUpperLimit = 110;
                    LegLiftHeightLowerLimit = 30;
                    TravelRotationYlimit = 30;
                    break;
                case GaitType.TripleTripod16:
                    //BodyPositionY = 75;
                    //LegLiftHeight = 80;
                    //GaitSpeedMs = 25;

                    GaitSpeedMax = 500;
                    GaitSpeedMin = 20;
                    TravelLengthZupperLimit = 170;
                    TravelLengthZlowerLimit = 80;
                    TravelLengthXlimit = 25;
                    LegLiftHeightUpperLimit = 110;
                    LegLiftHeightLowerLimit = 30;
                    TravelRotationYlimit = 25;
                    break;
                default:
                    //BodyPositionY = 90;
                    //LegLiftHeight = 35;
                    //GaitSpeedMs = 40;

                    GaitSpeedMax = 500;
                    GaitSpeedMin = 25;
                    TravelLengthZupperLimit = 170;
                    TravelLengthZlowerLimit = 80;
                    TravelLengthXlimit = 35;
                    TravelRotationYlimit = 30;
                    LegLiftHeightUpperLimit = 110;
                    LegLiftHeightLowerLimit = 30;
                    break;
            }
        }
    }
}