using System;
using System.Threading;
using Autonoceptor.Shared.Gps;
using Autonoceptor.Shared.Utilities;

namespace Autonoceptor.Host.Utility.GpsNav
{
    internal sealed class GpsNavUtility
    {
        private double _turnMagnitudeMax = 100; //Was 23000 then 21000
        //private double _foundWaypointTriggerInches = 40;//Was 45. I have a feeling this will need to be based on the calculated precision of the GPS fix. Perhaps go by HDOP? Then map those to some value?
        private int _speedModifier = 0;

        internal MoveRequest CalculateMoveRequest(GpsFixData waypoint, GpsFixData gpsFixData, CancellationToken cancellationToken)
        {
            var moveReq = new MoveRequest();

            var distanceForSpeedMap = 0d;

            var distanceAndHeading = GpsExtensions.GetDistanceAndHeadingToDestination(gpsFixData.Lat, gpsFixData.Lon, waypoint.Lat, waypoint.Lon);
            var distanceToWaypoint = distanceAndHeading[0];

            if (AtGate(gpsFixData, waypoint))
                return moveReq;

            var headingToWaypoint = distanceAndHeading[1];

            if (Math.Abs(distanceForSpeedMap) < .01)
                distanceForSpeedMap = distanceToWaypoint;

            var travelMagnitude = (int)distanceToWaypoint.Map(0, distanceForSpeedMap, 1000, 1600);

            //Adjust sensitivity of turn based on distance. These numbers will need to be adjusted.
            //var turnMagnitudeModifier = distanceToWaypoint.Map(0, distanceForSpeedMap, 1000, -1000); 

            moveReq.MovementMagnitude = travelMagnitude + _speedModifier;

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

        private bool AtGate(GpsFixData gpsFixData, GpsFixData currentWaypoint)
        {
            var distanceAndHeading = GpsExtensions.GetDistanceAndHeadingToDestination(gpsFixData.Lat, gpsFixData.Lon, currentWaypoint.Lat, currentWaypoint.Lon);
            var distanceToWaypoint = distanceAndHeading[0];

            if (distanceToWaypoint <= 28)
                return true;

            return false;
        }
    }
}