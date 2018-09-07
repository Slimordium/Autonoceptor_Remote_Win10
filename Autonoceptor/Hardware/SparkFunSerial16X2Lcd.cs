using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Windows.Devices.SerialCommunication;
using Windows.Storage.Streams;
using Nito.AsyncEx;
using NLog;

namespace Autonoceptor.Service.Hardware
{
    public class DisplayGroup
    {
        public int GroupId { get; set; }
        public string GroupName { get; set; }
        public Dictionary<int, string> DisplayItems = new Dictionary<int, string>();
        public int TopLine { get; set; } = 1;
    }

    /// <summary>
    /// SparkFun Serial 16x2 LCD
    /// </summary>
    public class SparkFunSerial16X2Lcd
    {
        private readonly byte[] _startOfFirstLine = {0xfe, 0x80};
        private readonly byte[] _startOfSecondLine = {0xfe, 0xc0};
        private DataWriter _outputStream;

        private readonly ILogger _logger = LogManager.GetCurrentClassLogger();

        private SerialDevice _lcdSerialDevice;

        private readonly ConcurrentDictionary<int, DisplayGroup> _displayGroups = new ConcurrentDictionary<int, DisplayGroup>();

        private readonly AsyncLock _asyncMutex = new AsyncLock();

        public async Task InitializeAsync()
        {
            _lcdSerialDevice = await SerialDeviceHelper.GetSerialDeviceAsync("DN051BLI", 9600, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50));
         
            if (_lcdSerialDevice == null)
                return;

            _logger.Log(LogLevel.Info, "Lcd Opened");

            _outputStream = new DataWriter(_lcdSerialDevice.OutputStream);
        }

        private volatile int _currentGroup;

        public async Task NextGroup()
        {
            //using (await _asyncMutex.LockAsync())
            {
                try
                {
                    _currentGroup++;

                    if (_currentGroup > _displayGroups.Count)
                        _currentGroup = 1;

                    if (_currentGroup < 1)
                        _currentGroup = _displayGroups.Count - 1;

                    var displayGroup = _displayGroups[_currentGroup];

                    if (displayGroup.DisplayItems.ContainsKey(displayGroup.TopLine))
                    {
                        await WriteAsync(displayGroup.DisplayItems[1], displayGroup.TopLine);
                    }

                    if (displayGroup.DisplayItems.ContainsKey(displayGroup.TopLine + 1))
                    {
                        await WriteAsync(displayGroup.DisplayItems[2], displayGroup.TopLine + 1);
                    }
                }
                catch (Exception e)
                {
                    _logger.Log(LogLevel.Error, e.Message);
                }
            }
        }

        public async Task NextLineGroup()
        {
            var displayGroup = _displayGroups[_currentGroup];

            if (displayGroup.TopLine + 2 >= displayGroup.DisplayItems.Count)
            {
                _displayGroups[_currentGroup].TopLine += 2;
            }
            else
            {
                return;
            }

            if (displayGroup.DisplayItems.ContainsKey(displayGroup.TopLine))
            {
                await WriteAsync(displayGroup.DisplayItems[1], displayGroup.TopLine);
            }

            if (displayGroup.DisplayItems.ContainsKey(displayGroup.TopLine + 1))
            {
                await WriteAsync(displayGroup.DisplayItems[2], displayGroup.TopLine + 1);
            }
        }

        public async Task PreviousLineGroup()
        {
            var displayGroup = _displayGroups[_currentGroup];

            if (displayGroup.TopLine - 2 >= 1)
            {
                _displayGroups[_currentGroup].TopLine -= 2;
            }
            else
            {
                return;
            }

            if (displayGroup.DisplayItems.ContainsKey(displayGroup.TopLine))
            {
                await WriteAsync(displayGroup.DisplayItems[1], displayGroup.TopLine);
            }

            if (displayGroup.DisplayItems.ContainsKey(displayGroup.TopLine + 1))
            {
                await WriteAsync(displayGroup.DisplayItems[2], displayGroup.TopLine + 1);
            }
        }

        public async Task PreviousGroup()
        {
            using (await _asyncMutex.LockAsync())
            {
                try
                {
                    _currentGroup--;

                    if (_currentGroup > _displayGroups.Count)
                        _currentGroup = 1;

                    if (_currentGroup < 1)
                        _currentGroup = _displayGroups.Count - 1;

                    var displayGroup = _displayGroups[_currentGroup];

                    if (displayGroup.DisplayItems.ContainsKey(displayGroup.TopLine))
                    {
                        await WriteAsync(displayGroup.DisplayItems[1], displayGroup.TopLine);
                    }

                    if (displayGroup.DisplayItems.ContainsKey(displayGroup.TopLine + 1))
                    {
                        await WriteAsync(displayGroup.DisplayItems[2], displayGroup.TopLine + 1);
                    }
                }
                catch (Exception e)
                {
                    _logger.Log(LogLevel.Error, e.Message);
                }
            }
        }

        public async Task<DisplayGroup> AddDisplayGroup(DisplayGroup displayGroup, bool display = false)
        {
            //using (await _asyncMutex.LockAsync())
            {
                try
                {
                    displayGroup.GroupId = _displayGroups.Count + 1;

                    _displayGroups.TryAdd(displayGroup.GroupId, displayGroup);

                    if (!display)
                        return displayGroup;

                    if (displayGroup.DisplayItems.ContainsKey(1))
                    {
                        await WriteAsync(displayGroup.DisplayItems[1], 1);
                    }

                    if (displayGroup.DisplayItems.ContainsKey(2))
                    {
                        await WriteAsync(displayGroup.DisplayItems[2], 2);
                    }
                }
                catch (Exception e)
                {
                    _logger.Log(LogLevel.Error, e.Message);
                }
            }

            return displayGroup;
        }

        public async Task UpdateDisplayGroup(DisplayGroup displayGroup, bool display = false)
        {
            //using (await _asyncMutex.LockAsync())
            {
                try
                {
                    if (!_displayGroups.ContainsKey(displayGroup.GroupId))
                    {
                        displayGroup.GroupId = _displayGroups.Count + 1;

                        _displayGroups.TryAdd(displayGroup.GroupId, displayGroup);
                    }
                    else
                    {
                        _displayGroups[displayGroup.GroupId] = displayGroup;
                    }

                    if (!display)
                        return;

                    if (displayGroup.DisplayItems.ContainsKey(1))
                    {
                        await WriteAsync(displayGroup.DisplayItems[1], 1);
                    }

                    if (displayGroup.DisplayItems.ContainsKey(2))
                    {
                        await WriteAsync(displayGroup.DisplayItems[2], 2);
                    }
                }
                catch (Exception e)
                {
                    _logger.Log(LogLevel.Error, e.Message);
                }
            }
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

            if (_outputStream == null)
            {
                _logger.Log(LogLevel.Error, "OutputStream is null");
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
                _logger.Log(LogLevel.Error, e.Message);
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

            using (await _asyncMutex.LockAsync())
            {
                await WriteAsync(text, _startOfFirstLine, true);
            }
        }

        /// <summary>
        /// Only overwrite the specified line on the LCD. Either line 1 or 2
        /// </summary>
        /// <param name="text"></param>
        /// <param name="line"></param>
        /// <returns></returns>
        public async Task WriteAsync(string text, int line)
        {
            using (await _asyncMutex.LockAsync())
            {
                if (line == 1)
                    await WriteToFirstLineAsync(text);

                if (line == 2)
                    await WriteToSecondLineAsync(text);
            }
        }
    }
}