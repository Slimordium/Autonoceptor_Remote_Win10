using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Autonoceptor.Service.Hardware.Lcd
{
    public class DisplayGroup
    {
        public DisplayGroup(DisplayGroupName displayGroupName)
        {
            DisplayLineItems.Add(1, string.Empty);
            DisplayLineItems.Add(2, string.Empty);

            DisplayLineItems[1] = displayGroupName.ToString();
            DisplayLineItems[2] = "NA";
        }
        public Dictionary<int, string> DisplayLineItems { get; } = new Dictionary<int, string>();
        public Action UpCallback { get; set; }
        public Action DownCallback { get; set; }
    }
}