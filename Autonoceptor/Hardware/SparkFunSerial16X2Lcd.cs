using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reactive.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.Devices.SerialCommunication;
using Windows.Storage.Streams;

namespace Autonoceptor.Service.Hardware
{

    /// <summary>
    /// SparkFun Serial 16x2 LCD
    /// </summary>
    public class SparkFunSerial16X2Lcd
    {
        private readonly byte[] _startOfFirstLine = {0xfe, 0x80};
        private readonly byte[] _startOfSecondLine = {0xfe, 0xc0};
        private DataWriter _outputStream;

        private SerialDevice _lcdSerialDevice;

        private IDisposable _writeDisposableOne;

        private IDisposable _writeDisposableTwo;

        public async Task InitializeAsync()
        {
            _lcdSerialDevice = await SerialDeviceHelper.GetSerialDeviceAsync("AH03FJHM", 9600, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50));
         
            if (_lcdSerialDevice == null)
                return;

            _outputStream = new DataWriter(_lcdSerialDevice.OutputStream);

            _writeDisposableOne = Observable.Interval(TimeSpan.FromMilliseconds(500)).Subscribe(async _ =>
                {
                    if (!_firstLineQueue.Any())
                        return;

                    try
                    {
                        if (_currentLineOneIndex > _firstLineQueue.Count)
                        {
                            _currentLineOneIndex = _firstLineQueue.Count - 1;
                        }

                        await WriteAsync(_firstLineQueue.ElementAt(_currentLineOneIndex).Value, 1);

                        _currentLineOneIndex++;
                    }
                    catch (Exception)
                    {
                        //
                    }
                });

            _writeDisposableTwo = Observable.Interval(TimeSpan.FromMilliseconds(500)).Subscribe(async _ =>
            {
                if (!_secondLineQueue.Any())
                    return;

                try
                {
                    if (_currentLineTwoIndex > _secondLineQueue.Count)
                    {
                        _currentLineTwoIndex = _secondLineQueue.Count - 1;
                    }

                    await WriteAsync(_secondLineQueue.ElementAt(_currentLineTwoIndex).Value, 2);

                    _currentLineTwoIndex++;
                }
                catch (Exception)
                {
                    //
                }
            });
        }

        private async Task WriteAsync(string text, byte[] line, bool clear)
        {
            if (string.IsNullOrEmpty(text) || _outputStream == null)
                return;

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

        public void AddOrUpdateWriteQueue(string text, string key, int line)
        {
            if (line == 1)
            {
                if (_firstLineQueue.ContainsKey(key))
                {
                    _firstLineQueue[key] = text;
                }
                else
                {
                    _firstLineQueue.Add(key, text);
                }
            }
            else
            {
                if (_secondLineQueue.ContainsKey(key))
                {
                    _secondLineQueue[key] = text;
                }
                else
                {
                    _secondLineQueue.Add(key, text);
                }
            }
        }

        public void ClearWriteQueue(int line)
        {
            if (line == 1)
                _firstLineQueue = new SortedDictionary<string, string>();
            else
                _secondLineQueue = new SortedDictionary<string, string>();
        }

        private int _currentLineOneIndex;

        private int _currentLineTwoIndex;

        private SortedDictionary<string, string> _firstLineQueue = new SortedDictionary<string, string>();

        private SortedDictionary<string, string> _secondLineQueue = new SortedDictionary<string, string>();
    }
}