﻿using System;
using System.Threading;
using System.Threading.Tasks;
using Autonoceptor.Hardware;
using Autonoceptor.Hardware.Lcd;
using Autonoceptor.Hardware.Maestro;
using Hardware.Xbox;
using NLog;
using RxMqtt.Client;
using RxMqtt.Shared;

namespace Autonoceptor.Vehicle
{
    public class Chassis
    {
        private readonly ILogger _logger = LogManager.GetLogger("Autonoceptor");

        protected const int RightSteeringPwm = 1800;
        protected const int CenterSteeringPwm = 1435; //was 1408
        protected const int LeftSteeringPwm = 1015;

        protected const int ReversePwm = 1072;
        protected const int StoppedPwm = 1471;
        protected const int ForwardPwm = 1856;

        protected const ushort MovementChannel = 0;
        protected const ushort SteeringChannel = 1;

        protected const ushort GpsNavEnabledChannel = 12;

        protected const ushort LidarServoChannel = 17;

        private bool _followingWaypoints;

        protected bool FollowingWaypoints
        {
            get => Volatile.Read(ref _followingWaypoints);
            set => Volatile.Write(ref _followingWaypoints, value);
        }

        protected Chassis(CancellationTokenSource cancellationTokenSource)
        {
            CancellationToken = cancellationTokenSource.Token;
        }

        public Odometer Odometer { get; private set; }
        public Imu Imu { get; private set; }
        public Tf02Lidar Lidar { get; private set; }
        protected Lcd Lcd { get; } = new Lcd();
        private PwmController PwmController { get; set; }
        public Gps Gps { get; private set; }
        protected XboxDevice XboxDevice { get; set; }

        protected MqttClient MqttClient { get; set; }

        protected CancellationToken CancellationToken { get; }

        private bool _stopped;
        protected bool Stopped
        {
            get => Volatile.Read(ref _stopped);
            set => Volatile.Write(ref _stopped, value);
        }

        protected IObservable<ChannelData> PwmObservable { get; private set; }

        protected async Task InitializeAsync()
        {
            _logger.Log(LogLevel.Info, "Initializing");

            Odometer = new Odometer(CancellationToken);
            Imu = new Imu(CancellationToken);
            Gps = new Gps();
            Lidar = new Tf02Lidar(CancellationToken);

            await Lcd.InitializeAsync();
            await Lcd.Update(GroupName.General, "Initializing...");

            PwmController = new PwmController(new ushort[] {12, 13, 14}); //Channel 12, 13 and 14 are inputs

            await PwmController.InitializeAsync(CancellationToken);

            PwmObservable = PwmController.GetObservable();

            await Imu.InitializeAsync();

            await Odometer.InitializeAsync();

            await InitializeXboxController();

            await Gps.InitializeAsync();

            await Lidar.InitializeAsync();

            //await InitializeMqtt("172.16.0.246");

            await Lcd.Update(GroupName.General, "Initialized");
        }

        protected async Task<bool> InitializeXboxController()
        {
            if (XboxDevice == null)
                XboxDevice = new XboxDevice();

            var initXbox = await XboxDevice.InitializeAsync(CancellationToken);

            _logger.Log(LogLevel.Info, $"XBox found => {initXbox}");

            return await Task.FromResult(initXbox);
        }

        protected async Task<uint> SetChannelValue(int value, ushort channel)
        {
            var returnValue = 0u;
            if (Stopped && channel == MovementChannel)// || channel == SteeringChannel))
            {
                await PwmController.SetChannelValue(StoppedPwm * 4, MovementChannel);
                //returnValue = await PwmController.SetChannelValue(0, SteeringChannel);
                returnValue = await PwmController.SetChannelValue(0, LidarServoChannel);
                return returnValue;
            }

            returnValue = await PwmController.SetChannelValue(value, channel);
            return returnValue;
        }

        protected async Task<int> GetChannelValue(ushort channel)
        {
            return await PwmController.GetChannelValue(channel);
        }

        public async Task<Status> InitializeMqtt(string hostnameOrIp)
        {
            if (MqttClient != null)
            {
                await Lcd.Update(GroupName.General, "Disposing", "MQTT Client");
                MqttClient.Dispose();
            }

            MqttClient = new MqttClient("autonoceptor", hostnameOrIp, 1883);

            var status = await MqttClient.InitializeAsync();

            await Lcd.Update(GroupName.General, $"MQTT {status}");

            return status;
        }
    }
}