using System;
using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.Devices.SerialCommunication;
using Windows.Storage.Streams;
using Nito.AsyncEx;
using NLog;

namespace Autonoceptor.Hardware.Lcd
{
    //    Serial.println("Green backlight set to 100%");
    //OpenLCD.write('|'); //Put LCD into setting mode
    //OpenLCD.write(158 + 29); //Set green backlight amount to 100%

    //    Serial.println("Mono/Red backlight set to 51%");
    //OpenLCD.write('|'); //Put LCD into setting mode
    //OpenLCD.write(128 + 15); //Set white/red backlight amount to 51%

    //    Serial.println("Blue backlight set to 51%");
    //OpenLCD.write('|'); //Put LCD into setting mode
    //OpenLCD.write(188 + 15); //Set blue backlight amount to 51%

    //    Serial.println("Blue backlight set to 100%");
    //OpenLCD.write('|'); //Put LCD into setting mode
    //OpenLCD.write(188 + 29); //Set blue backlight amount to 100%

    //ASCII / DEC / HEX
    //'|'    / 124 / 0x7C - Put into setting mode
    //Ctrl+c / 3 / 0x03 - Change width to 20
    //Ctrl+d / 4 / 0x04 - Change width to 16
    //Ctrl+e / 5 / 0x05 - Change lines to 4
    //Ctrl+f / 6 / 0x06 - Change lines to 2
    //Ctrl+g / 7 / 0x07 - Change lines to 1
    //Ctrl+h / 8 / 0x08 - Software reset of the system
    //Ctrl+i / 9 / 0x09 - Enable/disable splash screen
    //Ctrl+j / 10 / 0x0A - Save currently displayed text as splash

    //'-'    / 45 / 0x2D - Clear display.Move cursor to home position.
    // 128-157 / 0x80-0x9D - Set the primary backlight brightness. 128 = Off, 157 = 100%.
    // 158-187 / 0x9E-0xBB - Set the green backlight brightness. 158 = Off, 187 = 100%.
    // 188-217 / 0xBC-0xD9 - Set the blue backlight brightness. 188 = Off, 217 = 100%.
    //For example, to change the baud rate to 115200 send 124 followed by 18.
    //'+'    / 43 / 0x2B - Set Backlight to RGB value, follow + by 3 numbers 0 to 255, for the r, g and b values.
    //For example, to change the backlight to yellow send + followed by 255, 255 and 0.

    //'-'    / 45 / 0x2D - Clear display.Move cursor to home position.
    // 128-157 / 0x80-0x9D - Set the primary backlight brightness. 128 = Off, 157 = 100%.
    // 158-187 / 0x9E-0xBB - Set the green backlight brightness. 158 = Off, 187 = 100%.
    // 188-217 / 0xBC-0xD9 - Set the blue backlight brightness. 188 = Off, 217 = 100%.
    //For example, to change the baud rate to 115200 send 124 followed by 18.
    //'+'    / 43 / 0x2B - Set Backlight to RGB value, follow + by 3 numbers 0 to 255, for the r, g and b values.
    //For example, to change the backlight to yellow send + followed by 255, 255 and 0.

    //Turn on the backlight 100% so we can evaluate the contrast changes
    //    OpenLCD.write('|'); //Put LCD into setting mode
    //OpenLCD.write(128 + 29); //Set white/red backlight amount to 100%

    /// <summary>
    /// SparkFun Serial 16x2 LCD
    /// </summary>
    public class Lcd
    {
        private readonly byte[] _startOfFirstLine = {0xfe, 0x80};
        private readonly byte[] _startOfSecondLine = {0xfe, 0xc0};
        private DataWriter _outputStream;

        private readonly ILogger _logger = LogManager.GetCurrentClassLogger();

        private SerialDevice _lcdSerialDevice;

        private readonly ConcurrentDictionary<int, Group> _displayGroups = new ConcurrentDictionary<int, Group>();

        private readonly AsyncLock _asyncMutex = new AsyncLock();

        private volatile int _currentGroup;
        private IDisposable _updateObservable;

        public async Task InitializeAsync()
        {
            _lcdSerialDevice = await SerialDeviceHelper.GetSerialDeviceAsync("DN051BLI", 9600, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50));
         
            if (_lcdSerialDevice == null)
                return;

            _logger.Log(LogLevel.Info, "Lcd Opened");

            _outputStream = new DataWriter(_lcdSerialDevice.OutputStream);

            _updateObservable = Observable
                .Interval(TimeSpan.FromMilliseconds(350))
                .ObserveOnDispatcher()
                .Subscribe(
                    async _ =>
                    {
                        _displayGroups.TryGetValue(_currentGroup, out var displayGroup);

                        if (displayGroup == null)
                            return;

                        await ClearScreen();

                        await WriteToFirstLineAsync(displayGroup.DisplayLineItems[1]);
                        await WriteToSecondLineAsync(displayGroup.DisplayLineItems[2]);
                    });
        }

        public async Task Update(GroupName groupName, string line1, string line2 = "", bool display = false)
        {
            using (await _asyncMutex.LockAsync())
            {
                if (!_displayGroups.ContainsKey((int) groupName))
                {
                    _displayGroups.TryAdd((int) groupName, new Group(groupName));
                }

                var displayGroup = _displayGroups[(int) groupName];

                displayGroup.DisplayLineItems[1] = line1;

                if (string.IsNullOrEmpty(line2))
                {
                    displayGroup.DisplayLineItems[2] = "                ";
                }
                else
                {
                    displayGroup.DisplayLineItems[2] = line2;
                }

                if (display)
                {
                    _currentGroup = (int)groupName;

                    await WriteToFirstLineAsync(displayGroup.DisplayLineItems[1]);
                    await WriteToSecondLineAsync(displayGroup.DisplayLineItems[2]);
                }
            }
        }

        public async Task SetUpCallback(GroupName groupName, Func<string> callback)
        {
            using (await _asyncMutex.LockAsync())
            {
                if (!_displayGroups.ContainsKey((int)groupName))
                {
                    _displayGroups.TryAdd((int)groupName, new Group(groupName));
                }

                _displayGroups[(int)groupName].UpCallback = () => CallbackProxy(groupName, callback);
            }
        }

        public async Task SetDownCallback(GroupName groupName, Func<string> callback)
        {
            using (await _asyncMutex.LockAsync())
            {
                if (!_displayGroups.ContainsKey((int) groupName))
                {
                    _displayGroups.TryAdd((int) groupName, new Group(groupName));
                }

                _displayGroups[(int) groupName].DownCallback = () => CallbackProxy(groupName, callback);
            }
        }

        private void CallbackProxy(GroupName groupName, Func<string> callback)
        {
            if ((GroupName) _currentGroup != groupName)
                return;

            _displayGroups[(int)groupName].DisplayLineItems[2] = callback.Invoke();
        }

        public async Task ClearScreen()
        {
            _outputStream.WriteBytes(new byte[] { 0xfe, 0x2D});
            await _outputStream.StoreAsync();
        }

        public async Task InvokeUpCallback()
        {
            using (await _asyncMutex.LockAsync())
            {
                var displayGroupName = (GroupName) _currentGroup;

                if (!_displayGroups.ContainsKey((int)displayGroupName))
                {
                    _displayGroups.TryAdd((int)displayGroupName, new Group(displayGroupName));
                }

                var displayGroup = _displayGroups[(int)displayGroupName];

                displayGroup.UpCallback?.Invoke();
            }
        }

        public async Task InvokeDownCallback()
        {
            using (await _asyncMutex.LockAsync())
            {
                var displayGroupName = (GroupName)_currentGroup;

                if (!_displayGroups.ContainsKey((int)displayGroupName))
                {
                    _displayGroups.TryAdd((int)displayGroupName, new Group(displayGroupName));
                }

                var displayGroup = _displayGroups[(int)displayGroupName];

                displayGroup.DownCallback?.Invoke();
            }
        }

        public async Task NextGroup()
        {
            using (await _asyncMutex.LockAsync())
            {
                try
                {
                    _currentGroup++;

                    if (_currentGroup > 11)
                        _currentGroup = 0;

                    if (_currentGroup < 0)
                        _currentGroup = 11;

                    if (!_displayGroups.ContainsKey(_currentGroup))
                    {
                        _displayGroups.TryAdd(_currentGroup, new Group((GroupName)_currentGroup));

                        await ClearScreen();

                        await WriteToFirstLineAsync($"{(GroupName)_currentGroup}");
                        await WriteToSecondLineAsync($"... NA!");
                        return;
                    }

                    var displayGroup = _displayGroups[_currentGroup];

                    await ClearScreen();

                    await WriteToFirstLineAsync(displayGroup.DisplayLineItems[1]);
                    await WriteToSecondLineAsync(displayGroup.DisplayLineItems[2]);
                }
                catch (Exception e)
                {
                    _logger.Log(LogLevel.Error, e.Message);
                }
            }
        }

        public async Task PreviousGroup()
        {
            using (await _asyncMutex.LockAsync())
            {
                try
                {
                    _currentGroup--;

                    if (_currentGroup < 0)
                        _currentGroup = 11 ;

                    if (!_displayGroups.ContainsKey(_currentGroup))
                    {
                        _displayGroups.TryAdd(_currentGroup, new Group((GroupName) _currentGroup));

                        await ClearScreen();

                        await WriteToFirstLineAsync($"{(GroupName)_currentGroup}");
                        await WriteToSecondLineAsync($"... NA!");
                        return;
                    }

                    var displayGroup = _displayGroups[_currentGroup];

                    await ClearScreen();

                    await WriteToFirstLineAsync(displayGroup.DisplayLineItems[1]);
                    await WriteToSecondLineAsync(displayGroup.DisplayLineItems[2]);
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
            if (string.IsNullOrEmpty(text))
                return;

            await WriteAsync(text, _startOfFirstLine, false);
        }

        private async Task WriteToSecondLineAsync(string text)
        {
            if (string.IsNullOrEmpty(text))
                return;

            await WriteAsync(text, _startOfSecondLine, false);
        }
    }
}