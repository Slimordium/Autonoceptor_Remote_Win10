using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Streams;
using NLog;

namespace Autonoceptor.Vehicle
{
    internal static class FileExtensions
    {
        private static readonly ILogger _logger = LogManager.GetCurrentClassLogger();

        internal static async Task<string> ReadStringFromFile(this string filename)
        {
            var text = string.Empty;

            try
            {
                var storageFile = await ApplicationData.Current.LocalFolder.GetFileAsync(filename);
                var stream = await storageFile.OpenAsync(FileAccessMode.Read);
                var buffer = new Windows.Storage.Streams.Buffer((uint)stream.Size);

                await stream.ReadAsync(buffer, (uint)stream.Size, InputStreamOptions.None);

                if (buffer.Length > 0)
                    text = Encoding.UTF8.GetString(buffer.ToArray());
            }
            catch (Exception e)
            {
                _logger.Log(LogLevel.Error, e.Message);
            }

            return text;
        }

        //https://github.com/Microsoft/Windows-universal-samples/blob/master/Samples/ApplicationData/cs/Scenario1_Files.xaml.cs
        internal static async Task SaveStringToFile(string filename, string content)
        {
            var bytesToAppend = Encoding.UTF8.GetBytes(content.ToCharArray());

            try
            {
                var file = await ApplicationData.Current.LocalFolder.CreateFileAsync(filename, CreationCollisionOption.OpenIfExists).AsTask();

                using (var stream = await file.OpenStreamForWriteAsync())
                {
                    await stream.WriteAsync(new byte[stream.Length], 0, (int) stream.Length);

                    stream.Position = 0;
                    await stream.WriteAsync(bytesToAppend, 0, bytesToAppend.Length);
                }
            }
            catch (Exception e)
            {
                _logger.Log(LogLevel.Error, e.Message);
            }
        }
    }
}