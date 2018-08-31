﻿using System;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autonoceptor.Service.Hardware;
using Autonoceptor.Shared.Gps;
using Autonoceptor.Shared.Imu;
using Autonoceptor.Shared.Utilities;
using NLog;

namespace Autonoceptor.Host
{
    public class GpsNavigation : LidarNavOverride
    {
        private readonly ILogger _logger = LogManager.GetCurrentClassLogger();

        private int _currentWaypointIndex = 0;

        private bool _followingWaypoints;
        protected bool FollowingWaypoints
        {
            get => Volatile.Read(ref _followingWaypoints);
            set => Volatile.Write(ref _followingWaypoints, value);
        }

        public WaypointList Waypoints { get; set; } = new WaypointList();

        private int _steerMagnitudeScale = 180;

        private IDisposable _currentLocationUpdater;
        private IDisposable _gpsNavDisposable;
        private IDisposable _gpsNavSwitchDisposable;
        private IDisposable _odometerDisposable;

        public int WpTriggerDistance { get; set; }

        private double _gpsNavMoveMagnitude = 21;
        public double GpsNavMoveMagnitude
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

            if (moveRequest == null || Math.Abs(moveRequest.SteeringMagnitude) < 30)
                return;

            moveRequest.SteeringDirection = moveRequest.SteeringDirection;
            moveRequest.SteeringMagnitude = moveRequest.SteeringMagnitude * .6;

            await WriteToHardware(moveRequest);
        }

        public async Task WaypointFollowEnable(bool enabled)
        {
            if (FollowingWaypoints == enabled)
                return;

            FollowingWaypoints = enabled;

            if (enabled)
            {
                await Lcd.WriteAsync($"Started Nav to", 1);
                await Lcd.WriteAsync($"{Waypoints.Count} WPs", 2);

                _steerMagnitudeDecayDisposable?.Dispose();
                _gpsNavDisposable?.Dispose();
                _odometerDisposable?.Dispose();

                _gpsNavDisposable = Gps.GetObservable()
                    .ObserveOnDispatcher()
                    .Subscribe(async fix =>
                    {
                        await UpdateMoveRequest(fix);
                    });

                _odometerDisposable = Odometer.GetObservable()
                    .ObserveOnDispatcher()
                    .Subscribe(async odometerData =>
                    {
                        if (Math.Abs(_distanceToNextWaypoint) < 0 || _startOdometerData == null)
                            return;

                        var traveledInches = odometerData.InTraveled - _startOdometerData.InTraveled;

                        //We made it, yay.
                        if (traveledInches >= _distanceToNextWaypoint - 10)
                        {
                            _currentWaypointIndex++;

                            _distanceToNextWaypoint = 0;

                            _startOdometerData = null;

                            await Stop(); //Hopefully the GPS Nav will catch up in a second or two.

                            await CheckWaypointFollowFinished();
                        }

                    });

                return;
            }

            await EmergencyBrake(true);

            await Lcd.WriteAsync("GPS Nav stopped");

            _steerMagnitudeDecayDisposable?.Dispose();
            _gpsNavDisposable?.Dispose();
            _odometerDisposable?.Dispose();

            _steerMagnitudeDecayDisposable = null;
            _gpsNavDisposable = null;
            _odometerDisposable = null;
        }

        private double _distanceToNextWaypoint;

        private OdometerData _startOdometerData;

        private async Task<bool> CheckWaypointFollowFinished()
        {
            if (_currentWaypointIndex <= Waypoints.Count && FollowingWaypoints)
                return false;

            _logger.Log(LogLevel.Info, $"Nav finished {Waypoints.Count} WPs");

            await WaypointFollowEnable(false);
            return true;
        }

        private async Task UpdateMoveRequest(GpsFixData gpsFixData)
        {
            if (await CheckWaypointFollowFinished())
            {
                return;
            }

            var currentWp = Waypoints[_currentWaypointIndex];

            var moveReq = await GetMoveRequest(currentWp.GpsFixData, gpsFixData);

            moveReq.MovementMagnitude = GpsNavMoveMagnitude;

            await WriteToHardware(moveReq);

            await Lcd.WriteAsync($"{moveReq.SteeringDirection} {moveReq.SteeringMagnitude}", 1);
            await Lcd.WriteAsync($"Dist {moveReq.Distance} {_currentWaypointIndex}", 2);

            //This is wrong....
            //if (moveReq.Distance <= 36) //This should probably be slightly larger than the turning radius?
            //{
            //    _currentWaypointIndex++;
            //}
        }

        public async Task CheckImuGpsCalibration()
        {
            var gpsHeading = (await Gps.Get()).Heading;
            var imuHeading = (await RazorImu.Get()).Yaw;

            _logger.Log(LogLevel.Info, $"Yaw: {imuHeading}, Heading: {gpsHeading}, Diff: {gpsHeading - imuHeading}");

            if (gpsHeading - imuHeading < 2)
            {
                return;
            }

            var diff = imuHeading - gpsHeading;

            _logger.Log(LogLevel.Info, $"IMU Yaw correction set - Yaw: {imuHeading}, Heading: {gpsHeading}, Diff: {diff}");

            ImuData.YawCorrection = diff;
        }

        private async Task<MoveRequest> GetMoveRequest(GpsFixData waypoint, GpsFixData currentLocation)
        {
            var moveReq = new MoveRequest(MoveRequestType.Gps);

            var distanceAndHeading = GpsExtensions.GetDistanceAndHeadingToDestination(currentLocation.Lat, currentLocation.Lon, waypoint.Lat, waypoint.Lon);
            moveReq.Distance = distanceAndHeading[0];

            var headingToWaypoint = distanceAndHeading[1];

            if (_startOdometerData == null)
            {
                _startOdometerData = await Odometer.GetOdometerData();
                _distanceToNextWaypoint = moveReq.Distance;
            }

            //if (Math.Abs(distanceForSpeedMap) < .01)
            //distanceForSpeedMap = distanceToWaypoint;

            // var travelMagnitude = (int)distanceToWaypoint.Map(0, distanceToWaypoint, 1500, 1750);

            //Adjust sensitivity of turn based on distance. These numbers will need to be adjusted.
            //var turnMagnitudeModifier = distanceToWaypoint.Map(0, distanceToWaypoint, 1000, -1000); 

            //moveReq.MovementMagnitude = travelMagnitude;

            /*TODO: Idea! Use distance to way-point to setup a "pause" command at the estimated time of arrival. 
            Using that distance, also set estimated "decay" of steering magnitude. 
            Need to figure out how many FPS for a given travel magnitude
            For the list of way-points, pre-calculate turn direction and turn magnitude

            Example - 
            
            1. There is 60in to way-point, I travel about 2ft per second, so I should stop in 2.5 seconds so I don't 
            overshoot the way-point.

            2. At that point, the next GPS coordinates arrive, and I will either be where I estimate I should be, or re-calculate stop
            time and turn direction

            Goto 1.
            */

            //TODO: Need to do a while loop checking the Razor IMU yaw, until it matches the calculated heading of WP
            //TODO: On startup the Razor IMU yaw will NOT equal what the GPS thinks the heading is 


            var diff = currentLocation.Heading - headingToWaypoint;

            _logger.Log(LogLevel.Trace, $"Current Heading: {currentLocation.Heading}, Heading to WP: {headingToWaypoint}");
            _logger.Log(LogLevel.Trace, $"GPS Distance to WP: {moveReq.Distance}in");

            //Is it a right turn or left turn? Need logic
            //because we are based on heading, so we dont want to turn right, if we are at a heading of 10, and the target is at heading 350
            //
            if (diff < 0)
            {
                if (Math.Abs(diff) > 180)
                {
                    moveReq.SteeringDirection = SteeringDirection.Left;
                }
                else
                {
                    moveReq.SteeringDirection = SteeringDirection.Right;
                }
            }
            else
            {
                if (Math.Abs(diff) > 180)
                {
                    moveReq.SteeringDirection = SteeringDirection.Right;
                }
                else
                {
                    moveReq.SteeringDirection = SteeringDirection.Left;
                }
            }

            moveReq.SteeringMagnitude = Math.Abs(diff);

            //TODO: This is obviously not complete
            //TODO: Turn slowly until IMU Yaw is almost at waypoint... Also check ratio of yaw vs gps heading... hopefully the same
            while (true)
            {
                await Turn(moveReq.SteeringDirection, moveReq.SteeringMagnitude);

                await Move(MovementDirection.Forward, 12);
            }

            return moveReq;
        }


        private async Task Turn(SteeringDirection direction, double magnitude)
        {
            var steerValue = CenterPwm * 4;

            if (magnitude > 45)
                magnitude = 45;

            switch (direction)
            {
                case SteeringDirection.Left:
                    steerValue = Convert.ToUInt16(magnitude.Map(0, 45, CenterPwm, LeftPwmMax)) * 4;
                    break;
                case SteeringDirection.Right:
                    steerValue = Convert.ToUInt16(magnitude.Map(0, 45, CenterPwm, RightPwmMax)) * 4;
                    break;
            }

            await PwmController.SetChannelValue(steerValue, SteeringChannel);
        }

        //TODO: Set speed as percentage, control PWM value off of odometer pulse count, this will set ground speed instead of guessing
        private async Task Move(MovementDirection direction, double magnitude)
        {
            var moveValue = StoppedPwm * 4;

            switch (direction)
            {
                case MovementDirection.Forward:
                    moveValue = Convert.ToUInt16(magnitude.Map(0, 45, StoppedPwm, ForwardPwmMax)) * 4;
                    break;
                case MovementDirection.Reverse:
                    moveValue = Convert.ToUInt16(magnitude.Map(0, 45, StoppedPwm, ReversePwmMax)) * 4;
                    break;
            }

            await PwmController.SetChannelValue(moveValue, MovementChannel);
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

            //if (request.SteeringMagnitude < LeftPwmMax)
            //    request.SteeringMagnitude = LeftPwmMax;

            //if (request.SteeringMagnitude > RightPwmMax)
            //    request.SteeringMagnitude = RightPwmMax;

            //request.SteeringMagnitude = request.SteeringMagnitude * 4;

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