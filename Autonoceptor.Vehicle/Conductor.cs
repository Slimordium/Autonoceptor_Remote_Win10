﻿using System.Threading;
using Windows.UI.Xaml;
using Autonoceptor.Hardware.Lcd;
using NLog;

namespace Autonoceptor.Vehicle
{
    /// <summary>
    /// Any UI interaction would be routed through this class
    /// </summary>
    public class Conductor : XboxController
    {
        private readonly ILogger _logger = LogManager.GetCurrentClassLogger();

        public Conductor(CancellationTokenSource cancellationTokenSource, string brokerIpOrHostname)
            : base(cancellationTokenSource, brokerIpOrHostname)
        {
            Application.Current.UnhandledException += Current_UnhandledException;
            Application.Current.Suspending += Current_Suspending;
        }

        private async void Current_Suspending(object sender, Windows.ApplicationModel.SuspendingEventArgs e)
        {
            await Stop();
            await DisableServos();

            _logger.Log(LogLevel.Error, $"Suspending {e.SuspendingOperation.Deadline}");
        }

        private async void Current_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            await Lcd.Update(GroupName.General, $"Unhandled Exc", e.Message);

            _logger.Log(LogLevel.Error, e);
        }
    }
}