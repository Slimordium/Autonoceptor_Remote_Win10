using System.Threading;
using System.Threading.Tasks;
using Autonoceptor.Service.Hardware;
using Hardware.Xbox;
using RxMqtt.Client;
using RxMqtt.Shared;

namespace Autonoceptor.Host
{
    public class Chassis
    {
        protected Tf02Lidar Lidar { get; } = new Tf02Lidar();
        protected SparkFunSerial16X2Lcd Lcd { get; } = new SparkFunSerial16X2Lcd();
        protected MaestroPwmController PwmController { get; private set; }
        protected Gps Gps { get; } = new Gps();
        protected XboxDevice XboxDevice { get; set; }
        protected CancellationToken CancellationToken { get; }
        protected MqttClient MqttClient { get; set; }

        protected Chassis(CancellationTokenSource cancellationTokenSource)
        {
            CancellationToken = cancellationTokenSource.Token;
        }

        protected async Task InitializeAsync()
        {
            await Lcd.InitializeAsync();
            await Lcd.WriteAsync("Initializing...");

            PwmController = new MaestroPwmController(new ushort[]{ 12, 13, 14 }); //Channel 12, 13 and 14 are inputs

            await PwmController.InitializeAsync(CancellationToken);

            await InitializeXboxController();

            await Gps.InitializeAsync(CancellationToken);
            await Lidar.InitializeAsync(CancellationToken);

            await Lcd.WriteAsync("Initialized");
        }

        protected async Task<bool> InitializeXboxController()
        {
            if (XboxDevice == null)
                XboxDevice = new XboxDevice();

            var initXbox = await XboxDevice.InitializeAsync(CancellationToken);

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