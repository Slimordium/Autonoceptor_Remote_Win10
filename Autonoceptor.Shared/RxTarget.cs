using System;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using NLog;
using NLog.Targets;

namespace NLog.Targets.Rx
{
    [Target("RxTarget")]
    public class RxTarget : Target
    {
        public static IObservable<string> LogObservable { get; private set; }

        private readonly ISubject<string> _logSubject = new Subject<string>();

        protected override void InitializeTarget()
        {
            LogObservable = _logSubject.AsObservable();
        }

        protected override void Write(LogEventInfo logEvent)
        {
            _logSubject.OnNext(logEvent.Message);
        }
    }
}