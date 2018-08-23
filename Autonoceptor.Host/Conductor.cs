using System.Threading;

namespace Autonoceptor.Host
{
    /// <summary>
    /// Any UI interaction would be routed through this class
    /// </summary>
    public class Conductor : XboxController
    {
        public Conductor(CancellationTokenSource cancellationTokenSource, string brokerIpOrHostname) 
            : base(cancellationTokenSource, brokerIpOrHostname) { }
    }
}