using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reflection.Metadata.Ecma335;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Autonoceptor.Service.Hardware;
using Autonoceptor.Shared;
using Autonoceptor.Shared.Utilities;
using Nito.AsyncEx;
using NLog;

namespace Autonoceptor.Host
{

    public enum Sweep
    {
        Left,
        Right,
        Full,
        Center //Continually sweep 15 degrees(?) directly in front of the car
    }


    
}