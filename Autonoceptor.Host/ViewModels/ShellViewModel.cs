using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.ExtendedExecution;
using Windows.Devices.SerialCommunication;
using Windows.Storage.Streams;
using Windows.UI.Xaml;
using Autonoceptor.Host.Hardware;
using Autonoceptor.Shared.Gps;
using Autonoceptor.Shared.Utilities;
using Caliburn.Micro;
using Hardware.Xbox;
using Hardware.Xbox.Enums;
using Newtonsoft.Json;
using Nito.AsyncEx;

namespace Autonoceptor.Host.ViewModels
{
    public class ShellViewModel : Conductor<object>
    {
        private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();

        private SerialDevice _arduinoSerialDevice;

        private ExtendedExecutionSession _session;

        private IDisposable _sessionDisposable;

        private bool _started;

        private readonly AsyncProducerConsumerQueue<Task> _asyncProducerConsumerQueue = new AsyncProducerConsumerQueue<Task>();

        private XboxDevice XboxDevice;

        private IDisposable _xboxDisposable;
        private bool _streamFrames;
        private IDisposable _streamFrameDisposable;

        private DataWriter _arduinoOutputStream;
        private DataReader _arduinoInputStream;

        public string Cm { get; set; }
        public string Yaw { get; set; }
        public string Pitch { get; set; }
        public string Roll { get; set; }
        public string Rpm { get; set; }

        public ShellViewModel()
        {
            Application.Current.Suspending += CurrentOnSuspending;
            Application.Current.Resuming += CurrentOnResuming;

            _sessionDisposable = Observable.Interval(TimeSpan.FromMinutes(4))
                .Subscribe(async _ => { await RequestExtendedSession(); });

            //Automatically start...
            //Observable.Timer(TimeSpan.FromSeconds(2))
            //    .ObserveOnDispatcher()
            //    .Subscribe(async _ => { await StartConductor().ConfigureAwait(false); });

            //RxTarget.LogObservable.ObserveOnDispatcher().Subscribe(async e => { await AddToLog(e); });

            //Observable.Interval(TimeSpan.FromMilliseconds(100)).SubscribeOnDispatcher().Subscribe(b =>
            //{
            //    AddToLog(b.ToString());
            //});
        }

        public string Log => _stringBuilder.ToString();

        private readonly StringBuilder _stringBuilder = new StringBuilder();

        private void AddToLog(string entry)
        {
            _stringBuilder.Insert(0, $"{entry} {Environment.NewLine}");

            if (_stringBuilder.Length > 1500)
            {
                _stringBuilder.Remove(_stringBuilder.Length - 500, 499);
            }

            NotifyOfPropertyChange(nameof(Log));
        }

        private async Task<string> Read(uint length)
        {
            if (_arduinoInputStream == null)
                return "input stream is null";
        
            try
            {
                var inCount = await _arduinoInputStream.LoadAsync(length);

                if (inCount < 1)
                    return string.Empty;

                return _arduinoInputStream.ReadString(inCount);
            }
            catch (Exception e)
            {
                //AddToLog($"Read failed: {e.Message}");
                return string.Empty;
            }
        }

        private async Task Write(byte[] buffer)
        {
            if (_arduinoOutputStream == null)
                return;

            try
            {
                _arduinoOutputStream.WriteBytes(buffer.ToArray());
                var x = await _arduinoOutputStream.StoreAsync();
            }
            catch (Exception e)
            {
                //AddToLog($"Write failed: {e.Message}");
            }
        }

        public void DisposeCar()
        {
            AddToLog("Disposing...");

            _arduinoOutputStream?.Dispose();
            _arduinoInputStream?.Dispose();

            _arduinoSerialDevice?.Dispose();

            _arduinoOutputStream = null;
            _arduinoInputStream = null;

            _arduinoSerialDevice = null;

            DisposeXbox();

            _started = false;

            AddToLog("Disposed");
        }

        private async Task<uint> WriteMappedXboxValues(XboxData xboxData)
        {
            var moveMagnitude = 6000d; //Stopped
            var steerMagnitude = 6000d; //Center

            if (xboxData.RightTrigger > xboxData.LeftTrigger)
            {
                moveMagnitude = Math.Round(xboxData.RightTrigger.Map(0, 33000, 6000, 4400)); //Forward
            }
            else
            {
                moveMagnitude = Math.Round(xboxData.LeftTrigger.Map(0, 33000, 6000, 7500)); //Reverse
            }

            if (xboxData.RightStick.Direction == Direction.DownLeft ||
                xboxData.RightStick.Direction == Direction.UpLeft ||
                xboxData.RightStick.Direction == Direction.Left)
            {
                steerMagnitude = Math.Round(xboxData.RightStick.Magnitude.Map(0, 10000, 6000, 4000));
            }

            if (xboxData.RightStick.Direction == Direction.DownRight ||
                xboxData.RightStick.Direction == Direction.UpRight ||
                xboxData.RightStick.Direction == Direction.Right)
            {
                steerMagnitude = Math.Round(xboxData.RightStick.Magnitude.Map(0, 10000, 6000, 8000));
            }

            var writeBuffer = BuildCommandBuffer(Command.SetThrottlePwm, Convert.ToUInt16(moveMagnitude));

            writeBuffer.AddRange(BuildCommandBuffer(Command.SetSteeringPwm, Convert.ToUInt16(steerMagnitude)));

            //AddToLog(BitConverter.ToString(writeBuffer.ToArray()));

            _arduinoOutputStream.WriteBytes(writeBuffer.ToArray());

            await _arduinoOutputStream.StoreAsync();

            return await Task.FromResult(1u);
        }

        private List<byte> BuildCommandBuffer(Command command, ushort commandValue)
        {
            var writeBuffer = new List<byte>
            {
                0x7E,
                0x7E,
                (byte) command
            };

            writeBuffer.AddRange(BitConverter.GetBytes(commandValue));

            return writeBuffer;
        }

        public async Task GetFrame()
        {
            try
            {
                await Write(BuildCommandBuffer(Command.GetCurrentTelemetryFrame, 1).ToArray());

                var frameString = await Read(300);

                if (string.IsNullOrEmpty(frameString) || !frameString.StartsWith('{') || !frameString.EndsWith('\n'))
                    return;

                var frame = JsonConvert.DeserializeObject<AutonoDataFrame>(frameString);

                Cm = frame.CmToTarget.ToString();
                Yaw = frame.Yaw.ToString();
                Pitch = frame.Pitch.ToString();
                Roll = frame.Roll.ToString();
                Rpm = frame.RpmActual.ToString();

                NotifyOfPropertyChange(nameof(Cm));
                NotifyOfPropertyChange(nameof(Yaw));
                NotifyOfPropertyChange(nameof(Pitch));
                NotifyOfPropertyChange(nameof(Roll));
                NotifyOfPropertyChange(nameof(Rpm));
            }
            catch (Exception e)
            {
                AddToLog($"Get frame failed: {e.Message}");
            }
        }

        public async Task EnablePixy()
        {
            AddToLog("PIXY steering enabled");
            await Write(BuildCommandBuffer(Command.PixySteeringEnabled, 1).ToArray());
        }

        public async Task DisablePixy()
        {
            AddToLog("PIXY steering disabled");
            await Write(BuildCommandBuffer(Command.PixySteeringEnabled, 0).ToArray());
        }

        public async Task EnableLidar()
        {
            AddToLog("LIDAR safety enabled");
            await Write(BuildCommandBuffer(Command.LidarEnabled, 1).ToArray());
        }

        public async Task DisableLidar()
        {
            AddToLog("LIDAR safety disabled");
            await Write(BuildCommandBuffer(Command.LidarEnabled, 0).ToArray());
        }

        public async Task EnableFollow()
        {
            AddToLog("Follow enable");
            await Write(BuildCommandBuffer(Command.FollowMeEnabled, 1).ToArray());
        }

        public async Task DisableFollow()
        {
            AddToLog("Follow disable");
            await Write(BuildCommandBuffer(Command.FollowMeEnabled, 0).ToArray());
        }

        public void StreamFrames()
        {
            _streamFrames = !_streamFrames;

            AddToLog($"Streaming frames: {_streamFrames}");

            if (_streamFrames)
            {
                _streamFrameDisposable?.Dispose();

                _streamFrameDisposable = Observable
                    .Interval(TimeSpan.FromMilliseconds(125))
                    .ObserveOnDispatcher()
                    .Subscribe(
                        async _ =>
                        {
                            await _asyncProducerConsumerQueue.TryEnqueueAsync(GetFrame());
                        });
            }
            else
            {
                _streamFrameDisposable?.Dispose();
                _streamFrameDisposable = null;
            }
        }

        public async Task InitXbox()
        {
            AddToLog("XBox starting...");

            if (XboxDevice != null)
            {
                DisposeXbox();
            }

            XboxDevice = new XboxDevice();
            var init = await XboxDevice.InitializeAsync(CancellationToken.None);

            if (!init)
            {
                AddToLog("Could not find xBox controller");
                return;
            }

            _xboxDisposable = XboxDevice.GetObservable()
                .Where(xboxData => xboxData != null)
                .Sample(TimeSpan.FromMilliseconds(50))
                .ObserveOnDispatcher()
                .Subscribe(async xboxData =>
                {
                    try
                    {
                        await _asyncProducerConsumerQueue.TryEnqueueAsync(WriteMappedXboxValues(xboxData));
                    }
                    catch (Exception e)
                    {
                        AddToLog(e.Message);
                    }
                });

            AddToLog("XBox started");
        }

        private Task _queueTask;

        public void DisposeXbox()
        {
            XboxDevice?.Dispose();
            XboxDevice = null;

            _xboxDisposable?.Dispose();
            _xboxDisposable = null;

            AddToLog("XBox disposed");
        }

        public async Task InitCar()
        {
            if (_started)
                return;

            _started = true;

            AddToLog("Starting autonoceptor...");

            //543 - USB Cable
            //03FJ - FTDI

            

            _arduinoSerialDevice = await SerialDeviceHelper.GetSerialDeviceAsync("03FJ", 115200, TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(100));

            if (_arduinoSerialDevice == null)
            {
                AddToLog("Could not find arduino?");
                return;
            }

            AddToLog($"Found: {_arduinoSerialDevice.UsbVendorId} - {_arduinoSerialDevice.UsbProductId}");

            _arduinoInputStream = new DataReader(_arduinoSerialDevice.InputStream) { InputStreamOptions = InputStreamOptions.Partial };
            _arduinoOutputStream = new DataWriter(_arduinoSerialDevice.OutputStream);

            _queueTask = Task.Run(async () =>
            {
                foreach (var t in _asyncProducerConsumerQueue.GetConsumingEnumerable())
                {
                    await t.ConfigureAwait(false);
                }

            });

            //_queueTask.Start();

            await InitXbox();

            AddToLog("Started autonoceptor");

            //_gpsDisposable = _conductor
            //    .Gps
            //    .GetObservable()
            //    .Where(data => data != null)
            //    .ObserveOnDispatcher()
            //    .Subscribe(data =>
            //    {
            //        LatLon = data.ToString();
            //        NotifyOfPropertyChange(nameof(LatLon));
            //    });

        }

        private void CurrentOnResuming(object sender, object o)
        {
            _sessionDisposable?.Dispose();

            _sessionDisposable = Observable
                .Interval(TimeSpan.FromMinutes(4))
                .Subscribe(async _ =>
                {
                    await RequestExtendedSession(); 
                });

            //Observable.Timer(TimeSpan
            //    .FromSeconds(3))
            //    .ObserveOnDispatcher()
            //    .Subscribe(async _ => { await StartConductor().ConfigureAwait(false); });
        }

        private void NewSessionOnRevoked(object sender, ExtendedExecutionRevokedEventArgs args)
        {
            _session?.Dispose();
            _session = null;
        }

        private async Task RequestExtendedSession()
        {
            _session = new ExtendedExecutionSession {Reason = ExtendedExecutionReason.LocationTracking};

            _session.Revoked -= NewSessionOnRevoked;
            _session.Revoked += NewSessionOnRevoked;

            var sessionResult = await _session.RequestExtensionAsync();

            switch (sessionResult)
            {
                case ExtendedExecutionResult.Allowed:
                    //AddToLog("Session extended");
                    break;

                case ExtendedExecutionResult.Denied:
                    //AddToLog("Session extend denied");
                    break;
            }
        }

        private async void CurrentOnSuspending(object sender, SuspendingEventArgs suspendingEventArgs)
        {
            var deferral = suspendingEventArgs.SuspendingOperation.GetDeferral();

            _cancellationTokenSource.Cancel();

            await Task.Delay(1000);

            deferral.Complete();
        }
    }
}