using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Emgu.CV;
using Emgu.CV.Cuda;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Hexapi.Shared;
using Hexapi.Shared.OpenCv;
using Hexapi.Shared.Utilities;
using Newtonsoft.Json;
using RxMqtt.Client;
using CircleF = Emgu.CV.Structure.CircleF;
using LineSegment2D = Hexapi.Shared.OpenCv.LineSegment2D;
using PointF = System.Drawing.PointF;

namespace Hexapi.OpenCV
{
    class Program
    {
        private static MqttClient _mqttClient;
        private static long _safeWait;
        private static IDisposable _hexEyeDisposable;
        private static IDisposable _houghCircleParamsDisposable;
        private static IDisposable _cannyParamsDisposable;
        private static double _cannyHigh = 5;
        private static double _cannyLow = 50;
        private static HoughCircleParams _houghCircleParams = new HoughCircleParams();

        static async Task Main(string[] args)
        {
            Console.WriteLine($"CUDA Available: '{CudaInvoke.HasCuda}'");

            _mqttClient = new MqttClient("OpenCv", "127.0.0.1", 1883);

            var status = await _mqttClient.InitializeAsync();

            Console.WriteLine($"MQTT Connection => {status}");

            Console.WriteLine($"Subscribing to 'hex-eye', 'hough-circle-params'");

            _hexEyeDisposable = _mqttClient
                .GetPublishByteObservable("hex-eye")
                .Subscribe(async b =>
                {
                    if (Interlocked.Exchange(ref _safeWait, 1) == 1) //skip this image if not finished processing previous image
                        return;

                    await CudaDetector(b);
                });

            _houghCircleParamsDisposable = _mqttClient
                .GetPublishStringObservable("hough-circle-params")
                .Subscribe(b =>
                {
                    try
                    {
                        _houghCircleParams = JsonConvert.DeserializeObject<HoughCircleParams>(b);
                    }
                    catch (Exception)
                    {
                        //
                    }
                });

            _cannyParamsDisposable = _mqttClient
                .GetPublishStringObservable("canny-lines-params")
                .Subscribe(b =>
                {
                    try
                    {
                        var p = JsonConvert.DeserializeObject<double[]>(b);

                        _cannyHigh = p[0];
                        _cannyLow = p[1];
                    }
                    catch (Exception)
                    {
                        //
                    }
                });

            Console.WriteLine("Press any key to exit...");
            Console.ReadLine();

            _hexEyeDisposable?.Dispose();
            _houghCircleParamsDisposable?.Dispose();
        }

        private static async Task CudaDetector(byte[] imageBuffer)
        {
            using (var memoryStream = new MemoryStream(imageBuffer))
            using (var bmp = new Bitmap(memoryStream))
            {
                using (var img = new Image<Bgr, byte>(bmp))
                {
                    var rotatedImage = img.Rotate(180, new Bgr(Color.Black), false);

                    //Convert the image to grayscale and filter out the noise
                    using (var uimage = new Mat())
                    {
                        CvInvoke.CvtColor(rotatedImage, uimage, ColorConversion.Bgr2Gray);

                        //use image pyr to remove noise
                        using (var pyrDown = new Mat())
                        {
                            CvInvoke.PyrDown(uimage, pyrDown);
                            CvInvoke.PyrUp(pyrDown, uimage);

                            var gpuImage = new GpuMat();
                            gpuImage.Upload(uimage);
                            

                            //gpuImage.Bitmap.Save($"gpuMat.{DateTime.Now.Minute}.{DateTime.Now.Second}.jpg", ImageFormat.Jpeg);

                            await DetectCircles(gpuImage);

                            await DetectLines(gpuImage);
                        }

                    }
                }
            }

            Interlocked.Exchange(ref _safeWait, 0);
        }

        private static async Task DetectCircles(GpuMat gpuImage)
        {
            //using (var detector = new CudaHoughCirclesDetector(1, 10, 150, 60, 2, 140, 8)) //1, 20, 127, 60, 5, 400, 10
            using (var detector = new CudaHoughCirclesDetector(
                _houghCircleParams.Dp,
                _houghCircleParams.MinDistance,
                _houghCircleParams.CannyThreshold,
                _houghCircleParams.VotesThreshold,
                _houghCircleParams.MinRadius,
                _houghCircleParams.MaxRadius,
                _houghCircleParams.MaxCircles)) //1, 20, 127, 60, 5, 400, 10
            {
                try
                {
                    IOutputArray gpuResult = new GpuMat();

                    detector.Detect(gpuImage, gpuResult);

                    var gpuMat = (GpuMat)gpuResult;

                    var mat = new Mat();

                    gpuMat.Download(mat);

                    var circleBuffer = mat.GetData();

                    if (circleBuffer == null)
                    {
                        return;
                    }

                    var circles = new List<CircleF>();

                    for (var i = 0; i < circleBuffer.Length - 12;)
                    {
                        var x = BitConverter.ToSingle(circleBuffer, i);

                        i += 4;

                        var y = BitConverter.ToSingle(circleBuffer, i);

                        i += 4;

                        var r = BitConverter.ToSingle(circleBuffer, i);

                        i += 4;

                        Console.WriteLine($"X:{x} Y:{y} R:{r}");

                        //var circle = new CircleF(new PointF(Map(x, 0, 800, 800), Map(y)), r);
                        var circle = new CircleF(new PointF(x, y), r);

                        circles.Add(circle);
                    }

                    await _mqttClient.PublishAsync(JsonConvert.SerializeObject(circles), "opencv-circle");
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                }
            }
        }

        private static async Task DetectLines(GpuMat gpuImage)
        {
            using (var canny = new CudaCannyEdgeDetector(_cannyLow, _cannyHigh))
            {
                //using (var detector = new CudaHoughLinesDetector(1, (float)(Math.PI / 180), 5, false, 20)) //1000 is longest
                {
                    try
                    {
                        IOutputArray cannyResult = new GpuMat();

                        canny.Detect(gpuImage, cannyResult);

                        var gpuCannyMat = (GpuMat)cannyResult;

                        var cannyMat = new Mat();

                        gpuCannyMat.Download(cannyMat);

                        //gpuCannyMat.Bitmap.Save($"canny.{DateTime.Now.Minute}.{DateTime.Now.Second}.jpg", ImageFormat.Jpeg);


                        using (var stream = new MemoryStream())
                        {
                            gpuCannyMat.Bitmap.Save(stream, ImageFormat.Png);

                            await _mqttClient.PublishAsync(stream.ToArray(), "cannylines", TimeSpan.FromSeconds(5));
                        }


                        //IOutputArray gpuResult = new GpuMat();

                        //detector.Detect(cannyResult, gpuResult);
                        ////detector.Detect(gpuImage, gpuResult);

                        //var gpuMat = (GpuMat)gpuResult;

                        //var mat = new Mat();

                        //gpuMat.Download(mat);

                        ////var lineSegments = new List<LineSegment2D>();

                        ////var x1s = new List<double>();
                        ////var y1s = new List<double>();

                        ////var x2s = new List<double>();
                        ////var y2s = new List<double>();


                        //for (var i = 0; i < mat.Cols / 2 - 2; i++)
                        //{
                        //    //var rho = BitConverter.ToSingle(mat.Col(i).Row(0).GetData(), 0);
                        //    var rho = detector.Rho;

                        //    var theta = BitConverter.ToSingle(mat.Col(i).Row(1).GetData(), 0);

                        //    double a = Math.Cos(theta), b = Math.Sin(theta);
                        //    double x0 = a * rho, y0 = b * rho;

                        //    var x1 = x0 + 1000 * -b;
                        //    var y1 = y0 + 1000 * a;

                        //    var x2 = x0 - 1000 * -b;
                        //    var y2 = y0 - 1000 * a;

                        //    Console.WriteLine($"{ x1}, {y1}, {x2}, {y2}");
                        //}


                        //for (var i = 0; i < mat.Cols / 2 - 2; i++)
                        //{
                        //    var e1 = mat.Col(i).Row(0).GetData();

                        //    i++;

                        //    var e3 = mat.Col(i).Row(0).GetData();

                        //    double x1 = BitConverter.ToSingle(e1, 0);

                        //    double y1 = BitConverter.ToSingle(e3, 0);

                        //    x1s.Add(x1);

                        //    y1s.Add(y1);
                        //}

                        //for (var i = mat.Cols / 2; i < mat.Cols - 2; i++)
                        //{
                        //    var e1 = mat.Col(i).Row(0).GetData();

                        //    i++;

                        //    var e3 = mat.Col(i).Row(0).GetData();

                        //    double x2 = BitConverter.ToSingle(e1, 0);

                        //    double y2 = BitConverter.ToSingle(e3, 0);

                        //    x2s.Add(x2);

                        //    y2s.Add(y2);
                        //}

                        //for (int i = 0; i < mat.Cols / 2 / 2 -1; i++)
                        //{
                        //    Console.WriteLine($"{ x1s[i]}, {y1s[i]}, {x2s[i]}, {y2s[i]}");

                        //    var lineSegment = new LineSegment2D(new Point((int)x1s[i], (int)y1s[i]), new Point((int)x2s[i], (int)y2s[i]));

                        //    lineSegments.Add(lineSegment);
                        //}

                        //await _mqttClient.PublishAsync(JsonConvert.SerializeObject(lineSegments), "opencv-line");
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(e);
                    }
                }
            }
        }

    }
}
