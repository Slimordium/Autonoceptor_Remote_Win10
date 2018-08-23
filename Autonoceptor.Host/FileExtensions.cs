using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Autonoceptor.Host
{
    internal static class FileExtensions
    {
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
            catch (Exception)
            {
                //await Display.Write($"Read failed {filename}");
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

                //await Display.Write($"Write {file.Path}");

                using (var stream = await file.OpenStreamForWriteAsync())
                {
                    await stream.WriteAsync(new byte[stream.Length], 0, (int) stream.Length);

                    stream.Position = 0;
                    await stream.WriteAsync(bytesToAppend, 0, bytesToAppend.Length);
                }
            }
            catch (Exception)
            {
                //await Display.Write($"Save failed {filename}");
            }
        }
    }
}