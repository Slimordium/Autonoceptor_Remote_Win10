﻿using System;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autonoceptor.Shared.Gps;
using Autonoceptor.Shared.Utilities;

namespace Autonoceptor.Host
{
    public class GpsNavigation : LidarNavOverride
    {
        private int _currentWaypointIndex;

        private bool _followingWaypoints;
        protected bool FollowingWaypoints
        {
            get => Volatile.Read(ref _followingWaypoints);
            set => Volatile.Write(ref _followingWaypoints, value);
        }

        protected WaypointList Waypoints { get; set; } = new WaypointList();

        private int _steerMagnitudeScale = 180;

        private GpsFixData _currentLocation;
        protected GpsFixData CurrentLocation
        {
            get => Volatile.Read(ref _currentLocation);
            set => Volatile.Write(ref _currentLocation, value);
        } 

        private IDisposable _currentLocationUpdater;
        private IDisposable _gpsNavDisposable;
        private IDisposable _gpsNavSwitchDisposable;

        private double _gpsNavMoveMagnitude = 30;
        protected double GpsNavMoveMagnitude
        {
            get => Volatile.Read(ref _gpsNavMoveMagnitude);
            set => Volatile.Write(ref _gpsNavMoveMagnitude, value);
        }

        private IDisposable _steerMagnitudeDecayDisposable;

        private MoveRequest _currentGpsMoveRequest;

        protected MoveRequest CurrentGpsMoveRequest
        {
            get => Volatile.Read(ref _currentGpsMoveRequest);
            private set => Volatile.Write(ref _currentGpsMoveRequest, value);
        }

        protected GpsNavigation(CancellationTokenSource cancellationTokenSource, string brokerHostnameOrIp) 
            : base(cancellationTokenSource, brokerHostnameOrIp)
        {
            cancellationTokenSource.Token.Register(async () =>
            {
                await WaypointFollowEnable(false);
            });
        }

        protected new async Task InitializeAsync()
        {
            await base.InitializeAsync();

            _steerMagnitudeDecayDisposable = Observable
                .Interval(TimeSpan.FromMilliseconds(100))
                .ObserveOnDispatcher()
                .Subscribe(async _ =>
                {
                    await DecaySteeringMagnitude();
                });

            _currentLocationUpdater = Gps
                .GetObservable()
                .ObserveOnDispatcher()
                .Subscribe(fix =>
                {
                    CurrentLocation = fix; 
                });

            _gpsNavSwitchDisposable = PwmController.GetObservable()
                .Where(channel => channel.ChannelId == GpsNavEnabledChannel)
                .ObserveOnDispatcher()
                .Subscribe(async channelData =>
                {
                    await WaypointFollowEnable(channelData.DigitalValue);
                });
        }

        private async Task DecaySteeringMagnitude()
        {
            var moveRequest = CurrentGpsMoveRequest;

            if (moveRequest == null || Math.Abs(moveRequest.SteeringMagnitude) < 5)
                return;

            moveRequest.SteeringDirection = moveRequest.SteeringDirection;
            moveRequest.SteeringMagnitude = moveRequest.SteeringMagnitude * .6;

            await WriteToHardware(moveRequest);
        }

        public async Task WaypointFollowEnable(bool enabled)
        {
            FollowingWaypoints = enabled;

            if (enabled)
            {
                await Lcd.WriteAsync($"Started Nav to", 1);
                await Lcd.WriteAsync($"{Waypoints.Count} WPs", 2);

                _gpsNavDisposable = Gps.GetObservable()
                    .ObserveOnDispatcher()
                    .Subscribe(async fix =>
                    {
                        await UpdateMoveRequest(fix);
                    });

                return;
            }

            await Lcd.WriteAsync("GPS Nav stopped");

            _gpsNavDisposable?.Dispose();
        }

        private async Task UpdateMoveRequest(GpsFixData gpsFixData)
        {
            if (_currentWaypointIndex >= Waypoints.Count || !FollowingWaypoints)
            {
                await EmergencyBrake();

                await WaypointFollowEnable(false);
                return;
            }

            var currentWp = Waypoints[_currentWaypointIndex];

            var moveReq = GetMoveRequest(currentWp, gpsFixData);

            moveReq.MovementMagnitude = GpsNavMoveMagnitude;

            await WriteToHardware(moveReq);

            await Lcd.WriteAsync($"{moveReq.SteeringDirection} {moveReq.SteeringMagnitude}", 1);
            await Lcd.WriteAsync($"Dist {moveReq.Distance} {_currentWaypointIndex}", 2);

            if (moveReq.Distance <= 40) //This should probably be slightly larger than the turning radius?
            {
                _currentWaypointIndex++;
            }
        }

        private MoveRequest GetMoveRequest(GpsFixData waypoint, GpsFixData currentLocation)
        {
            var moveReq = new MoveRequest();

            var distanceAndHeading = GpsExtensions.GetDistanceAndHeadingToDestination(currentLocation.Lat, currentLocation.Lon, waypoint.Lat, waypoint.Lon);
            moveReq.Distance = distanceAndHeading[0];

            var headingToWaypoint = distanceAndHeading[1];

            //if (Math.Abs(distanceForSpeedMap) < .01)
            //distanceForSpeedMap = distanceToWaypoint;

            // var travelMagnitude = (int)distanceToWaypoint.Map(0, distanceToWaypoint, 1500, 1750);

            //Adjust sensitivity of turn based on distance. These numbers will need to be adjusted.
            //var turnMagnitudeModifier = distanceToWaypoint.Map(0, distanceToWaypoint, 1000, -1000); 

            //moveReq.MovementMagnitude = travelMagnitude;

            var diff = currentLocation.Heading - headingToWaypoint;

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

        private async Task WriteToHardware(MoveRequest request)
        {
            CurrentGpsMoveRequest = request;

            var moveValue = StoppedPwm * 4;
            var steerValue = CenterPwm * 4;

            if (request.SteeringMagnitude > 45)
                request.SteeringMagnitude = 45;

            switch (request.SteeringDirection)
            {
                case SteeringDirection.Left:
                    steerValue = Convert.ToUInt16(request.SteeringMagnitude.Map(0, 45, CenterPwm, LeftPwmMax)) * 4;
                    break;
                case SteeringDirection.Right:
                    steerValue = Convert.ToUInt16(request.SteeringMagnitude.Map(0, 45, CenterPwm, RightPwmMax)) * 4;
                    break;
            }

            await PwmController.SetChannelValue(steerValue, SteeringChannel);

            switch (request.MovementDirection)
            {
                case MovementDirection.Forward:
                    moveValue = Convert.ToUInt16(request.MovementMagnitude.Map(0, 45, StoppedPwm, ForwardPwmMax)) * 4;
                    break;
                case MovementDirection.Reverse:
                    moveValue = Convert.ToUInt16(request.MovementMagnitude.Map(0, 45, StoppedPwm, ReversePwmMax)) * 4;
                    break;
            }

            await PwmController.SetChannelValue(moveValue, MovementChannel);
        }
    }
}