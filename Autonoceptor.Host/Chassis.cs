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

        public Odometer Odometer { get; private set; }
        protected RazorImu RazorImu { get; private set; }
        protected Tf02Lidar  Lidar { get; private set; }
        protected SparkFunSerial16X2Lcd Lcd { get; } = new SparkFunSerial16X2Lcd();
        protected MaestroPwmController PwmController { get; private set; }
        protected Gps Gps { get; private set; }
        protected XboxDevice XboxDevice { get; set; }
  
        protected MqttClient MqttClient { get; set; }

        protected CancellationToken CancellationToken { get; }

        protected Chassis(CancellationTokenSource cancellationTokenSource)
        {
            CancellationToken = cancellationTokenSource.Token;
        }

        protected async Task InitializeAsync()
        {
            _logger.Log(LogLevel.Info, "Initializing");

            Odometer = new Odometer(CancellationToken);
            RazorImu = new RazorImu(CancellationToken);
            Gps = new Gps(CancellationToken);
            Lidar = new Tf02Lidar(CancellationToken);

            await Lcd.InitializeAsync();
            await Lcd.WriteAsync("Initializing...");

            PwmController = new MaestroPwmController(new ushort[]{ 12, 13, 14 }); //Channel 12, 13 and 14 are inputs

            await PwmController.InitializeAsync(CancellationToken);

            await RazorImu.InitializeAsync();

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

        protected async Task<Status> InitializeMqtt(string hostnameOrIp)
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