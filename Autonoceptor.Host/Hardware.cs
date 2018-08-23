using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Devices.HumanInterfaceDevice;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Storage.Streams;
using Windows.UI.Xaml.Controls;
using Autonoceptor.Service.Hardware;
using Autonoceptor.Shared.Gps;
using Autonoceptor.Shared.Utilities;
using Hardware.Xbox;
using Hardware.Xbox.Enums;
using Newtonsoft.Json;
using RxMqtt.Client;
using RxMqtt.Shared;

namespace Autonoceptor.Host
{
    public class Hardware
    {
        protected readonly Tf02Lidar _lidar = new Tf02Lidar();
        protected readonly SparkFunSerial16X2Lcd _lcd = new SparkFunSerial16X2Lcd();
        protected MaestroPwmController _maestroPwm;
        protected readonly Gps _gps = new Gps();
        protected XboxDevice _xboxDevice = new XboxDevice();
        protected CancellationToken _cancellationToken;

        public async Task InitializeAsync(CancellationTokenSource cancellationTokenSource)
        {
            _cancellationToken = cancellationTokenSource.Token;

            await _lcd.InitializeAsync();
            await _lcd.WriteAsync("Initializing...");

            _maestroPwm = new MaestroPwmController(new ushort[]{12, 13, 14 });

            await _maestroPwm.InitializeAsync(_cancellationToken);

            var initXbox = await _xboxDevice.InitializeAsync(_cancellationToken);

            if (!initXbox)
            {
                _xboxDevice = null;
            }

            await _gps.InitializeAsync(_cancellationToken);
            await _lidar.InitializeAsync(_cancellationToken);
        }
    }
}