using System;
using System.Threading;
using Autonoceptor.Shared.Gps;
using Autonoceptor.Shared.Utilities;

namespace Autonoceptor.Host.Utility.GpsNav
{
    internal static class GpsNavUtility
    {
        private static double _turnMagnitudeMax = 100;
        //private double _foundWaypointTriggerInches = 40;//Was 45. I have a feeling this will need to be based on the calculated precision of the GPS fix. Perhaps go by HDOP? Then map those to some value?
        //private int _speedModifier = 0;

        internal static MoveRequest CalculateMoveRequest(GpsFixData waypoint, GpsFixData gpsFixData, CancellationToken cancellationToken)
        {
            var moveReq = new MoveRequest();

            var distanceForSpeedMap = 0d;

            var distanceAndHeading = GpsExtensions.GetDistanceAndHeadingToDestination(gpsFixData.Lat, gpsFixData.Lon, waypoint.Lat, waypoint.Lon);
            var distanceToWaypoint = distanceAndHeading[0];

            var headingToWaypoint = distanceAndHeading[1];

            if (Math.Abs(distanceForSpeedMap) < .01)
                distanceForSpeedMap = distanceToWaypoint;

            var travelMagnitude = (int)distanceToWaypoint.Map(0, distanceForSpeedMap, 1500, 1650);

            //Adjust sensitivity of turn based on distance. These numbers will need to be adjusted.
            //var turnMagnitudeModifier = distanceToWaypoint.Map(0, distanceForSpeedMap, 1000, -1000); 

            moveReq.MovementMagnitude = travelMagnitude;

            var diff = gpsFixData.Heading - headingToWaypoint;

            if (diff < 0)
            {
                if (Math.Abs(diff) > 180)
                {
                    moveReq.SteeringDirection = SteeringDirection.Left;
                    moveReq.SteeringMagnitude = (int)Math.Abs(diff + 360).Map(0, 360, 0, _turnMagnitudeMax);
                }
                else
                {
                    moveReq.SteeringDirection = SteeringDirection.Right;
                    moveReq.SteeringMagnitude = (int)Math.Abs(diff).Map(0, 360, 0, _turnMagnitudeMax);
                }
            }
            else
            {
                if (Math.Abs(diff) > 180)
                {
                    moveReq.SteeringDirection = SteeringDirection.Right;
                    moveReq.SteeringMagnitude = (int)Math.Abs(diff - 360).Map(0, 360, 0, _turnMagnitudeMax);
                }
                else
                {
                    moveReq.SteeringDirection = SteeringDirection.Left;
                    moveReq.SteeringMagnitude = (int)Math.Abs(diff).Map(0, 360, 0, _turnMagnitudeMax);
                }
            }

            if (distanceToWaypoint <= 35)
            {
                moveReq.MovementMagnitude = 0;
                moveReq.MovementDirection = MovementDirection.Stopped;
            }

            return moveReq;
        }
    }
}