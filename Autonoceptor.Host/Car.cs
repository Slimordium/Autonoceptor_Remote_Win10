﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autonoceptor.Shared.Utilities;
using Newtonsoft.Json;
using NLog;
using RxMqtt.Shared;

namespace Autonoceptor.Host
{
    public class Car : Chassis
    {
        private readonly ILogger _logger = LogManager.GetCurrentClassLogger();


        protected const ushort _extraInputChannel = 13;
        private const ushort _enableMqttChannel = 14;

        private IDisposable _enableMqttDisposable;

        private List<IDisposable> _sensorDisposables = new List<IDisposable>();
        private IDisposable _speedControllerDisposable;

        public WaypointList Waypoints { get; set; } = new WaypointList();

        protected string BrokerHostnameOrIp { get; set; }

        protected Car(CancellationTokenSource cancellationTokenSource, string brokerHostnameOrIp) 
            : base(cancellationTokenSource)
        {
            BrokerHostnameOrIp = brokerHostnameOrIp;

            cancellationTokenSource.Token.Register(async () => { await Stop(); });
        }

        protected new async Task InitializeAsync()
        {
            await base.InitializeAsync();

            _enableMqttDisposable = PwmObservable
                .Where(channel => channel.ChannelId == _enableMqttChannel)
                .ObserveOnDispatcher()
                .Subscribe(
                    async channel =>
                    {
                        if (channel.DigitalValue)
                        {
                            var status = await InitializeMqtt(BrokerHostnameOrIp);

                            if (status == Status.Initialized)
                                ConfigureSensorPublish();

                            return;
                        }

                        MqttClient?.Dispose();
                        MqttClient = null;
                    });

            await Stop();
            await DisableServos();
        }

        protected void ConfigureSensorPublish()
        {
            if (MqttClient == null)
                return;

            if (_sensorDisposables.Any())
            {
                _sensorDisposables.ForEach(disposable =>
                {
                    disposable.Dispose();
                });
            }

            _sensorDisposables = new List<IDisposable>();

            _sensorDisposables.Add(
                Gps.GetObservable()
                    .ObserveOnDispatcher()
                    .Subscribe(async gpsFixData =>
                    {
                        try
                        {
                            await MqttClient.PublishAsync(JsonConvert.SerializeObject(gpsFixData), "autono-gps");
                        }
                        catch (Exception)
                        {
                            //Yum
                        }
                    }));

            _sensorDisposables.Add(
                Lidar.GetObservable()
                    .ObserveOnDispatcher()
                    .Subscribe(async lidarData =>
                    {
                        try
                        {
                            await MqttClient.PublishAsync(JsonConvert.SerializeObject(lidarData), "autono-lidar");
                        }
                        catch (Exception)
                        {
                            //Yum
                        }
                    }));
        }

        private double _pulseCountPerUpdate;
        private double _moveMagnitude;

        private async Task UpdateMoveMagnitude()
        {
            var moveMagnitude = Volatile.Read(ref _moveMagnitude);
            var pulseCountPerUpdate = Volatile.Read(ref _pulseCountPerUpdate);

            //-------------Update distance traveled, this should solve overshoot when gps fix is not due for another second.
            //var startDistance = GpsNavParameters.GetOdometerTraveledDistance();
            //var distanceToWaypoint = GpsNavParameters.GetDistanceToWaypoint();

            //var remainingDistance = distanceToWaypoint - (odometer.InTraveled - startDistance);

            //GpsNavParameters.SetDistanceToWaypoint(remainingDistance);
            //-----------------------------------------------------

            var pulseCount = 0;

            for (var i = 0; i <= 2; i++)
            {
                var odometer = await Odometer.GetLatest();

                pulseCount = pulseCount + odometer.PulseCount;
            }

            pulseCount = pulseCount / 3; //Average...

            //Give it some wiggle room
            if (pulseCount < pulseCountPerUpdate + 30 && pulseCount > pulseCountPerUpdate - 50)
            {
                return;
            }

            if (pulseCount < pulseCountPerUpdate)
                moveMagnitude = moveMagnitude + 15;

            if (pulseCount > pulseCountPerUpdate)
            {
                if (moveMagnitude > 60)
                {
                    moveMagnitude = moveMagnitude - 5;
                }
                else if (moveMagnitude > 50)
                {
                    moveMagnitude = moveMagnitude - 2;
                }
                else
                {
                    moveMagnitude = moveMagnitude - .6;
                }
            }

            if (moveMagnitude > 70)
                moveMagnitude = 70;

            if (moveMagnitude < 0)
                moveMagnitude = 0;

            await SetVehicleTorque(MovementDirection.Forward, moveMagnitude);

            Volatile.Write(ref _moveMagnitude, moveMagnitude);
        }

        protected async Task SetVehicleTorque(MovementDirection direction, double magnitude)
        {
            var moveValue = StoppedPwm * 4;

            switch (direction)
            {
                case MovementDirection.Forward:
                    moveValue = Convert.ToUInt16(magnitude.Map(0, 80, StoppedPwm, ForwardPwmMax)) * 4;
                    break;
                case MovementDirection.Reverse:
                    moveValue = Convert.ToUInt16(magnitude.Map(0, 80, StoppedPwm, ReversePwmMax)) * 4;
                    break;
            }

            await SetChannelValue(moveValue, MovementChannel);
        }

        public void EnableCruiseControl(int pulseCountPerUpdateInterval)
        {
            _speedControllerDisposable?.Dispose();

            Volatile.Write(ref _moveMagnitude, 0);
            Volatile.Write(ref _pulseCountPerUpdate, pulseCountPerUpdateInterval);

            _speedControllerDisposable = Observable
                .Interval(TimeSpan.FromMilliseconds(50))
                .ObserveOnDispatcher()
                .Subscribe(async _ =>
                {
                    await UpdateMoveMagnitude();
                });
        }

        public void DisableCruiseControl()
        {
            Volatile.Write(ref _moveMagnitude, 0);
            Volatile.Write(ref _pulseCountPerUpdate, 0);

            _speedControllerDisposable?.Dispose();
            _speedControllerDisposable = null;
        }

        public void SetCruiseControl(int pulseCountPerUpdateInterval)
        {
            Volatile.Write(ref _moveMagnitude, 0);
            Volatile.Write(ref _pulseCountPerUpdate, pulseCountPerUpdateInterval);
        }

        public async Task Stop(bool isCanceled = false)
        {
            if (isCanceled)
            {
                Stopped = false;

                await Lcd.WriteAsync("Started");

                _logger.Log(LogLevel.Info, "Started");

                return;
            }

            await SetChannelValue(StoppedPwm * 4, MovementChannel);
            await SetChannelValue(0, SteeringChannel);

            if (Stopped)
                return;

            Stopped = true;

            await Lcd.WriteAsync("Stopped");
            _logger.Log(LogLevel.Info, "Stopped");
        }

        public async Task DisableServos()
        {
            await SetChannelValue(0, MovementChannel);
            await SetChannelValue(0, SteeringChannel);

            await Task.Delay(100);

            _logger.Log(LogLevel.Info, "PWM Off");
        }
    }
}