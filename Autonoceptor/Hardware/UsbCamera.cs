using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Subjects;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.Core;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;
using Windows.UI.Core;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Imaging;
using NLog;

namespace Autonoceptor.Service.Hardware{
    public class UsbCamera
    {
        private static readonly ILogger _logger = LogManager.GetCurrentClassLogger();

        private readonly int _videoProfile = 8;

        public UsbCamera(int videoProfile)
        {
            _videoProfile = videoProfile;
        }

        public ISubject<byte[]> ImageCaptureSubject { get;  } = new Subject<byte[]>();

        private async Task<byte[]> EncodedBytes(SoftwareBitmap soft, Guid encoderId)
        {
            byte[] array = null;

            // First: Use an encoder to copy from SoftwareBitmap to an in-mem stream (FlushAsync)
            // Next:  Use ReadAsync on the in-mem stream to get byte[] array

            using (var ms = new InMemoryRandomAccessStream())
            {
                BitmapEncoder encoder = await BitmapEncoder.CreateAsync(encoderId, ms);
                encoder.SetSoftwareBitmap(soft);

                try
                {
                    await encoder.FlushAsync();
                }
                catch (Exception ex) { return new byte[0]; }

                array = new byte[ms.Size];
                await ms.ReadAsync(array.AsBuffer(), (uint)ms.Size, InputStreamOptions.None);
            }
            return array;
        }

        public Dictionary<int, string> Resolutions { get; } = new Dictionary<int, string>();

        public async Task<bool> StartAsync(CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<bool>();

            await CoreApplication.MainView.CoreWindow.Dispatcher.RunAsync(CoreDispatcherPriority.Normal,
            async () =>
            {
                var mediaCapture = new MediaCapture();

                await mediaCapture.InitializeAsync();

                var resolutions = mediaCapture.VideoDeviceController.GetAvailableMediaStreamProperties(MediaStreamType.VideoPreview).ToList();

                for (var i = 0;i <= resolutions.Count;i++)
                {
                    var p = (VideoEncodingProperties) resolutions[i];
                    Resolutions.Add(i, $"{p.Width}x{p.Height}, {p.FrameRate.Numerator / p.FrameRate.Denominator} fps, {p.Subtype}");
                }

                // set resolution
                await mediaCapture.VideoDeviceController.SetMediaStreamPropertiesAsync(MediaStreamType.VideoPreview, resolutions[_videoProfile]); //2, 8, 9 - 60fps = better

                var captureElement = new CaptureElement {Source = mediaCapture};

                await mediaCapture.StartPreviewAsync();

                var props = mediaCapture.VideoDeviceController.GetMediaStreamProperties(MediaStreamType.VideoPreview);

                await mediaCapture.SetEncodingPropertiesAsync(MediaStreamType.VideoPreview, props, null);

                while (!cancellationToken.IsCancellationRequested)
                {
                    using (var stream = new InMemoryRandomAccessStream())
                    {
                        try
                        {
                            await mediaCapture.CapturePhotoToStreamAsync(ImageEncodingProperties.CreateJpeg(), stream);
                        }
                        catch (Exception e)
                        {
                            await Task.Delay(1000);
                            _logger.Log(LogLevel.Warn, e.Message);
                            continue;
                        }
                       
                        try
                        {
                            using (var reader = new DataReader(stream.GetInputStreamAt(0)))
                            {
                                var bytes = new byte[stream.Size];
                                await reader.LoadAsync((uint)stream.Size);
                                reader.ReadBytes(bytes);

                                ImageCaptureSubject.OnNext(bytes);
                            }
                        }
                        catch (Exception e)
                        {
                            _logger.Log(LogLevel.Warn, $"ImageCapture => {e.Message}");
                        }
                    }
                }

                tcs.SetResult(true);
            });

            return await tcs.Task;
        }

        private static async Task<IRandomAccessStream> Reencode(IRandomAccessStream stream, PhotoOrientation photoOrientation)
        {
            IRandomAccessStream randomAccessStream;

            using (var inputStream = stream)
            {
                var decoder = await BitmapDecoder.CreateAsync(inputStream);

                using (var outputStream = new InMemoryRandomAccessStream())
                {
                    var pixelData = await decoder.GetPixelDataAsync(
                                                 BitmapPixelFormat.Rgba8,
                                                 BitmapAlphaMode.Straight,
                                                 new BitmapTransform { ScaledHeight = decoder.PixelHeight / 2, ScaledWidth = decoder.PixelWidth / 2 },
                                                 ExifOrientationMode.RespectExifOrientation,
                                                 ColorManagementMode.DoNotColorManage);

                    var encoder = await BitmapEncoder.CreateForTranscodingAsync(outputStream, decoder);

                    encoder.SetPixelData(BitmapPixelFormat.Rgba8, BitmapAlphaMode.Premultiplied, decoder.PixelWidth / 2, decoder.PixelHeight / 2, 96, 96, pixelData.DetachPixelData());

                    var properties = new BitmapPropertySet
                    {
                        { "System.Photo.Orientation", new BitmapTypedValue(photoOrientation, PropertyType.UInt16) },
                    };

                    await encoder.BitmapProperties.SetPropertiesAsync(properties);
                    await encoder.FlushAsync();

                    randomAccessStream = outputStream.CloneStream();
                }
            }

            return randomAccessStream;
        }

    }
}