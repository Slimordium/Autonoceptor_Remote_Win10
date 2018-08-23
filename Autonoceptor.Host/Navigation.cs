using System;
using System.Threading;
using System.Threading.Tasks;
using Autonoceptor.Shared.Gps;
using Autonoceptor.Shared.Utilities;

namespace Autonoceptor.Host
{
    public class Navigation : Car
    {
        private int _waypointIndex;

        private bool _followingWaypoints;

        private WaypointList _waypointList = new WaypointList();

        private static int _steerMagnitudeScale = 180;

        private async Task UpdateNav(GpsFixData gpsFixData)
        {
            if (_waypointIndex >= _waypointList.Count || !Volatile.Read(ref _followingWaypoints))
            {
                await _lcd.WriteAsync($"Nav complete {_waypointIndex}");
                return;
            }

            var currentWp = _waypointList[_waypointIndex];

            var moveReq = CalculateMoveRequest(currentWp, gpsFixData);

            //Override for now, 15 = 10%
            moveReq.MovementMagnitude = 30;
            //moveReq.SteeringMagnitude = 100;

            await RequestMove(moveReq);

            //var distance = _waypointList.GetInchesToNextWaypoint(_waypointIndex); //TODO: This seems fairly useless

            await _lcd.WriteAsync($"{moveReq.SteeringDirection} {moveReq.SteeringMagnitude}", 1);
            await _lcd.WriteAsync($"Dist {moveReq.Distance} {_waypointIndex}", 2);

            if (moveReq.Distance <= 40) //This should probably be slightly larger than the turning radius?
            {
                _waypointIndex++;
            }

            if (_waypointIndex >= _waypointList.Count)
            {
                await EmergencyStop();

                Volatile.Write(ref _followingWaypoints, false);
            }
        }

        public static MoveRequest CalculateMoveRequest(GpsFixData waypoint, GpsFixData gpsFixData)
        {
            var moveReq = new MoveRequest();

            var distanceAndHeading = GpsExtensions.GetDistanceAndHeadingToDestination(gpsFixData.Lat, gpsFixData.Lon, waypoint.Lat, waypoint.Lon);
            moveReq.Distance = distanceAndHeading[0];

            var headingToWaypoint = distanceAndHeading[1];

            //if (Math.Abs(distanceForSpeedMap) < .01)
            //distanceForSpeedMap = distanceToWaypoint;

            // var travelMagnitude = (int)distanceToWaypoint.Map(0, distanceToWaypoint, 1500, 1750);

            //Adjust sensitivity of turn based on distance. These numbers will need to be adjusted.
            //var turnMagnitudeModifier = distanceToWaypoint.Map(0, distanceToWaypoint, 1000, -1000); 

            //moveReq.MovementMagnitude = travelMagnitude;

            var diff = gpsFixData.Heading - headingToWaypoint;

            if (diff < 0)
            {
                if (Math.Abs(diff) > 180)
                {
                    moveReq.SteeringDirection = SteeringDirection.Left;
                    moveReq.SteeringMagnitude = (int)Math.Abs(diff + 360).Map(0, 360, 0, _steerMagnitudeScale);
                }
                else
                {
                    moveReq.SteeringDirection = SteeringDirection.Right;
                    moveReq.SteeringMagnitude = (int)Math.Abs(diff).Map(0, 360, 0, _steerMagnitudeScale);
                }
            }
            else
            {
                if (Math.Abs(diff) > 180)
                {
                    moveReq.SteeringDirection = SteeringDirection.Right;
                    moveReq.SteeringMagnitude = (int)Math.Abs(diff - 360).Map(0, 360, 0, _steerMagnitudeScale);
                }
                else
                {
                    moveReq.SteeringDirection = SteeringDirection.Left;
                    moveReq.SteeringMagnitude = (int)Math.Abs(diff).Map(0, 360, 0, _steerMagnitudeScale);
                }
            }

            return moveReq;
        }
    }
}