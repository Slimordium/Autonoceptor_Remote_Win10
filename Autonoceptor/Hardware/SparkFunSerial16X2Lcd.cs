using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reactive.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.SerialCommunication;
using Windows.Storage.Streams;

namespace Autonoceptor.Service.Hardware
{
    public class DisplayItem
    {
        public object GroupId { get; set; }

        public int LineNumber { get; set; } = 1;

        public string Text { get; set; }
    }

    /// <summary>
    /// SparkFun Serial 16x2 LCD
    /// </summary>
    public class SparkFunSerial16X2Lcd
    {
        private readonly byte[] _startOfFirstLine = {0xfe, 0x80};
        private readonly byte[] _startOfSecondLine = {0xfe, 0xc0};
        private DataWriter _outputStream;

        private SerialDevice _lcdSerialDevice;

        private IDisposable _writeDisposable;

        private readonly List<DisplayItem> _displayItems = new List<DisplayItem>();

        private object _selectedDisplayGroup;

        private readonly SemaphoreSlim _semaphoreSlim = new SemaphoreSlim(1, 1);

        public async Task InitializeAsync()
        {
            _lcdSerialDevice = await SerialDeviceHelper.GetSerialDeviceAsync("AH03FJHM", 9600, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50));
         
            if (_lcdSerialDevice == null)
                return;

            _outputStream = new DataWriter(_lcdSerialDevice.OutputStream);

            _writeDisposable = Observable.Interval(TimeSpan.FromMilliseconds(1000)).Subscribe(async _ =>
            {
                await _semaphoreSlim.WaitAsync();

                var selectedGroup = Volatile.Read(ref _selectedDisplayGroup);

                if (selectedGroup != null)
                {
                    var item1 = _displayItems.FirstOrDefault(i => i.GroupId == selectedGroup && i.LineNumber == 1);
                    var item2 = _displayItems.FirstOrDefault(i => i.GroupId == selectedGroup && i.LineNumber == 2);

                    if (item1 != null)
                    {
                        await WriteAsync(item1.Text, 1);
                    }
                    if (item2 != null)
                    {
                        await WriteAsync(item2.Text, 2);
                    }
                }

                _semaphoreSlim.Release(1);
            });
        }

        public async Task AddOrUpdateItem(DisplayItem displayItem)
        {
            if (displayItem.GroupId == null)
                return; //Lets not tell anyone

            await _semaphoreSlim.WaitAsync();

            if (_displayItems.Any(i => i.GroupId == displayItem.GroupId && i.LineNumber == displayItem.LineNumber))
            {
                _displayItems.Add(displayItem);
            }
            else
            {
                _displayItems.First(i => i.GroupId == displayItem.GroupId && i.LineNumber == displayItem.LineNumber).Text = displayItem.Text;
            }

            _semaphoreSlim.Release(1);
        }

        public void SetCurrentDisplayGroup(object o)
        {
            Volatile.Write(ref _selectedDisplayGroup, o);
        }

        private async Task WriteAsync(string text, byte[] line, bool clear)
        {
            if (string.IsNullOrEmpty(text) || _outputStream == null)
                return;

            if (text.Length > 16)
                text = text.Substring(0, 16);

            var stringBuilder = new StringBuilder();
            stringBuilder.Append(text);

            var count = 0;

            if (!clear)
                count = 16 - text.Length;
            else
                count = 32 - text.Length;

            if (count > 0)
            {
                for (var i = 0; i < count; i++)
                {
                    stringBuilder.Append(' ');
                }
            }

            if (_outputStream == null)// || _lcdSerialDevice == null)
            {
                Debug.WriteLine(text);
                return;
            }

            try
            {
                _outputStream.WriteBytes(line);
                _outputStream.WriteString(stringBuilder.ToString());
                await _outputStream.StoreAsync().AsTask();
            }
            catch (Exception e)
            {
                Debug.WriteLine(e.Message);
            }
        }

        private async Task WriteToFirstLineAsync(string text)
        {
            if (text == null)
                return;

            await WriteAsync(text, _startOfFirstLine, false);
        }

        private async Task WriteToSecondLineAsync(string text)
        {
            if (text == null)
                return;

            await WriteAsync(text, _startOfSecondLine, false);
        }

        /// <summary>
        /// Overwrites all text on display
        /// </summary>
        /// <param name="text"></param>
        /// <returns></returns>
        public async Task WriteAsync(string text)
        {
            if (string.IsNullOrEmpty(text))
                return;

            await WriteAsync(text, _startOfFirstLine, true);
        }

        /// <summary>
        /// Only overwrite the specified line on the LCD. Either line 1 or 2
        /// </summary>
        /// <param name="text"></param>
        /// <param name="line"></param>
        /// <returns></returns>
        public async Task WriteAsync(string text, int line)
        {
            if (line == 1)
                await WriteToFirstLineAsync(text);

            if (line == 2)
                await WriteToSecondLineAsync(text);
        }


    }
}