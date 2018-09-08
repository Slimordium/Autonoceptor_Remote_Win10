using System;
using System.Threading;
using System.Threading.Tasks;
using Autonoceptor.Service.Hardware;
using Hardware.Xbox;
using NLog;
using RxMqtt.Client;
using RxMqtt.Shared;

namespace Autonoceptor.Host
{
    public class Chassis
    {
        private readonly ILogger _logger = LogManager.GetLogger("Autonoceptor");

        protected const int RightPwmMax = 1800;
        protected const int CenterPwm = 1465; //was 1408
        protected const int LeftPwmMax = 1015;

        protected const int ReversePwmMax = 1072;
        protected const int StoppedPwm = 1471;
        protected const int ForwardPwmMax = 1856;

        protected const ushort MovementChannel = 0;
        protected const ushort SteeringChannel = 1;

        protected const ushort GpsNavEnabledChannel = 12;

        protected Chassis(CancellationTokenSource cancellationTokenSource)
        {
            CancellationToken = cancellationTokenSource.Token;
        }

        public Odometer Odometer { get; private set; }
        public Imu Imu { get; private set; }
        public Tf02Lidar Lidar { get; private set; }
        protected SparkFunSerial16X2Lcd Lcd { get; } = new SparkFunSerial16X2Lcd();
        private MaestroPwmController PwmController { get; set; }
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
            Gps = new Gps(CancellationToken);
            Lidar = new Tf02Lidar(CancellationToken);

            await Lcd.InitializeAsync();
            await Lcd.WriteAsync("Initializing...");

            PwmController = new MaestroPwmController(new ushort[] {12, 13, 14}); //Channel 12, 13 and 14 are inputs

            await PwmController.InitializeAsync(CancellationToken);

            PwmObservable = PwmController.GetObservable();

            await Imu.InitializeAsync();

            await Odometer.InitializeAsync();

            await InitializeXboxController();

            await Gps.InitializeAsync();

            await Lidar.InitializeAsync();

            await Lcd.WriteAsync("Initialized");
        }

        protected async Task<bool> InitializeXboxController()
        {
            if (XboxDevice == null)
                XboxDevice = new XboxDevice();

            var initXbox = await XboxDevice.InitializeAsync(CancellationToken);

            _logger.Log(LogLevel.Info, $"XBox found => {initXbox}");

            return await Task.FromResult(initXbox);
        }

        protected void DisposeLcdWriters()
        {
            Lcd.DisposeLcdUpdate();
        }

        protected void ConfigureLcdWriters()
        {
            Lcd.ConfigureLcdWriters();
        }

        protected async Task SetChannelValue(int value, ushort channel)
        {
            if (Stopped && (channel == MovementChannel || channel == SteeringChannel))
            {
                await PwmController.SetChannelValue(StoppedPwm * 4, MovementChannel);
                await PwmController.SetChannelValue(0, SteeringChannel);
                return;
            }

            await PwmController.SetChannelValue(value, channel);
        }

        protected async Task<int> GetChannelValue(ushort channel)
        {
            return await PwmController.GetChannelValue(channel);
        }

        public async Task<Status> InitializeMqtt(string hostnameOrIp)
        {
            if (MqttClient != null)
            {
                await Lcd.WriteAsync("Disposing", 1);
                await Lcd.WriteAsync("MQTT Client", 2);
                MqttClient.Dispose();
            }

            MqttClient = new MqttClient("autonoceptor", hostnameOrIp, 1883);

            var status = await MqttClient.InitializeAsync();

            await Lcd.WriteAsync($"MQTT {status}");

            return status;
        }
    }
}