using System.IO;
using System.Reactive.Subjects;
using System.Text;

namespace Autonoceptor.Shared
{
    public class SystemConsoleRedirect : TextWriter
    {
        public ISubject<string> LogSubject { get; } = new Subject<string>();

        public override void Write(char value)
        {
            //Do something

        }

        public override void WriteLine(string value)
        {
            LogSubject.OnNext(value);
        }

        public override void Write(string value)
        {
            LogSubject.OnNext(value);
        }

        public override Encoding Encoding => Encoding.ASCII;
    }
}