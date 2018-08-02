using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading;
using System.Diagnostics;
using System.Reactive.Subjects;
using Autonoceptor.Shared.Gps;
using Autonoceptor.Shared.Ik;
using Autonoceptor.Shared.Utilities;

namespace Autonoceptor.Service.Navigation
{
    internal sealed class GpsNavigator
    {
        //private readonly IkFilter _ikFilter;
        //private readonly Hardware.Gps _gps;
        private List<GpsFixData> _waypoints;
        //private readonly SparkFunSerial16X2Lcd _display;

        internal GpsNavigator()
        {
            //_ikFilter = ikFilter;
            //_gps = gps;
            //_display = display;
        }

        internal async Task StartAsync(CancellationToken token, Action action)
        {
            //_waypoints = await GpsExtensions.LoadWaypoints();

            //await _display.WriteAsync($"{_waypoints.Count} waypoints");

            foreach (var wp in _waypoints)
            {
                if (Math.Abs(wp.Lat) < 1 || Math.Abs(wp.Lon) < 1)
                    continue;

                await NavigateToWaypoint(wp, token);

                if (token.IsCancellationRequested)
                    break;
            }

            action?.Invoke();
            //await _ikFilter.RequestTravel(TravelDirection.Stop, 0);
        }

        private static double _lastSavedLat;
        private static double _lastSavedLon;

        internal async Task SaveWaypoint()
        {


            //if (Math.Abs(gpsFixData.Lat - _lastSavedLat) < 1 && Math.Abs(gpsFixData.Lon - _lastSavedLon) < 1)
            //    return;

            //_lastSavedLat = gpsFixData.Lat;
            //_lastSavedLon = gpsFixData.Lon;

            //await _display.WriteAsync("Saving...", 2);

            //await FileExtensions.SaveStringToFile("waypoints.txt", gpsFixData.ToString());

            //await _display.WriteAsync("Saved", 2);
        }

        internal IObservable<GpsFixData> GpsFixObservable { get; set; }

        internal ISubject<IkParams> IkParamSubject { get; set; } = new BehaviorSubject<IkParams>(new IkParams());

        internal async Task<bool> NavigateToWaypoint(GpsFixData currentWaypoint, CancellationToken cancelationToken)
        {
            var gpsFixData = new GpsFixData();

            GpsFixObservable.Subscribe(d => gpsFixData = d);

            var distanceAndHeading = GpsExtensions.GetDistanceAndHeadingToDestination(gpsFixData.Lat, gpsFixData.Lon, currentWaypoint.Lat, currentWaypoint.Lon);
            var distanceToWaypoint = distanceAndHeading[0];
            var headingToWaypoint = distanceAndHeading[1];

            var travelLengthZ = -130;

            while (distanceToWaypoint > 10) 
            {
                if (cancelationToken.IsCancellationRequested)
                    return false;

                var degDiff = Math.Abs(headingToWaypoint - gpsFixData.Heading); //How far do we need to turn?
                var turnMagnitude = degDiff.Map(0, 359, 0, 25);

                if (turnMagnitude > 25)
                    turnMagnitude = 25;

                RequestMove(gpsFixData.Heading, headingToWaypoint, turnMagnitude, travelLengthZ);

                //gpsFixData = Gps.CurrentGpsFixData;

                distanceAndHeading = GpsExtensions.GetDistanceAndHeadingToDestination(gpsFixData.Lat, gpsFixData.Lon, currentWaypoint.Lat, currentWaypoint.Lon);
                distanceToWaypoint = distanceAndHeading[0];
                headingToWaypoint = distanceAndHeading[1];

                if (cancelationToken.IsCancellationRequested)
                    return false;

                //await _display.WriteAsync($"DWP{distanceToWaypoint} HWP{headingToWaypoint} H{gpsFixData.Heading}", 2);
            }

            //await _ikFilter.RequestTravel(TravelDirection.Stop, 0);

            //await _display.WriteAsync("At waypoint...", 1);
            //await _display.WriteAsync($"D:{distanceToWaypoint} H:{headingToWaypoint}", 2);

            return true;
        }

        private void RequestMove(double currentHeading, double headingToWaypoint, double turnMagnitude, double travelLengthZ)
        {
            var ikParams = new IkParams();
            ikParams.LengthZ = travelLengthZ;
            ikParams.GaitSpeedMs = 20;

            if (currentHeading < 180 && headingToWaypoint > 270)
            {
                //_ikFilter.RequestMovement(20, 0, travelLengthZ, -turnMagnitude);

                ikParams.RotationY = -turnMagnitude;
                IkParamSubject.OnNext(ikParams);
                return;
            }

            if (currentHeading > 180 && headingToWaypoint < 90)
            {
                //_ikFilter.RequestMovement(20, 0, travelLengthZ, turnMagnitude);

                ikParams.RotationY = turnMagnitude;
                IkParamSubject.OnNext(ikParams);
                return;
            }


            if (currentHeading > headingToWaypoint)
            {
                //_ikFilter.RequestMovement(20, 0, travelLengthZ, -turnMagnitude);

                ikParams.RotationY = -turnMagnitude;
            }
            else
            {
                //_ikFilter.RequestMovement(20, 0, travelLengthZ, turnMagnitude);

                ikParams.RotationY = turnMagnitude;
            }

            IkParamSubject.OnNext(ikParams);
        }
    }
}