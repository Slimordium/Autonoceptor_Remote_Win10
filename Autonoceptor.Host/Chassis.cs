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

        protected Chassis(CancellationTokenSource cancellationTokenSource)
        {
            CancellationToken = cancellationTokenSource.Token;
        }

        public Odometer Odometer { get; private set; }
        public Imu Imu { get; private set; }
        protected Tf02Lidar Lidar { get; private set; }
        protected SparkFunSerial16X2Lcd Lcd { get; } = new SparkFunSerial16X2Lcd();
        protected MaestroPwmController PwmController { get; private set; }
        public Gps Gps { get; private set; }
        protected XboxDevice XboxDevice { get; set; }

        protected MqttClient MqttClient { get; set; }

        protected CancellationToken CancellationToken { get; }

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