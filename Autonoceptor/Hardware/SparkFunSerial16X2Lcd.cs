using System;
using System.Collections.Concurrent;
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
    public class DisplayGroup
    {
        public int GroupId { get; set; }
        public Dictionary<int, string> DisplayItems = new Dictionary<int, string>();
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

        private readonly ConcurrentDictionary<int, DisplayGroup> _displayGroups = new ConcurrentDictionary<int, DisplayGroup>();

        private int _selectedGroupIndex;

        private readonly SemaphoreSlim _semaphoreSlim = new SemaphoreSlim(1, 1);

        public async Task InitializeAsync()
        {
            _lcdSerialDevice = await SerialDeviceHelper.GetSerialDeviceAsync("AH03FJHM", 9600, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50));
         
            if (_lcdSerialDevice == null)
                return;

            _outputStream = new DataWriter(_lcdSerialDevice.OutputStream);
        }

        public void AddDisplayGroup(DisplayGroup displayGroup)
        {
            _displayGroups.TryAdd(_displayGroups.Count + 1, displayGroup);
        }

        //public void UpdateDisplayGroup(DisplayGroup displayGroup)
        //{
        //    _displayGroups.AddOrUpdate(displayGroup.GroupId, displayGroup)
        //}

        //public async Task IncrementGroup()
        //{
        //    _selectedGroupIndex++;

        //    if (_selectedGroupIndex > _displayGroups.Count)
        //    {
        //        _selectedGroupIndex = 0;
        //    }

        //    var dg = _displayGroups.Where(d => d.GroupId == _selectedGroupIndex).ToList();

        //    var lineOne = dg.FirstOrDefault(d => d.LineNumber == 1);
        //    var lineTwo = dg.FirstOrDefault(d => d.LineNumber == 2);

        //    if (lineOne != null)
        //        await WriteAsync(lineOne.Text, 1);

        //    if (lineTwo != null)
        //        await WriteAsync(lineTwo.Text, 2);
        //}

        //public async Task DecrementGroup()
        //{
        //    _selectedGroupIndex--;

        //    if (_selectedGroupIndex < 0)
        //    {
        //        _selectedGroupIndex = _displayGroups.Count;
        //    }

        //    var dg = _displayGroups.Where(d => d.GroupId == _selectedGroupIndex).ToList();

        //    var lineOne = dg.FirstOrDefault(d => d.LineNumber == 1);
        //    var lineTwo = dg.FirstOrDefault(d => d.LineNumber == 2);

        //    if (lineOne != null)
        //        await WriteAsync(lineOne.Text, 1);

        //    if (lineTwo != null)
        //        await WriteAsync(lineTwo.Text, 2);
        //}

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