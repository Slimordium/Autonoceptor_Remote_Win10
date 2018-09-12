using System;
using System.Collections.Generic;

namespace Autonoceptor.Service.Hardware.Lcd
{
    public class DisplayGroup
    {
        public DisplayGroup(DisplayGroupName displayGroupName)
        {
            GroupId = (int) displayGroupName;
            DisplayLineItems.Add(1, string.Empty);
            DisplayLineItems.Add(2, string.Empty);

            DisplayLineItems[1] = displayGroupName.ToString();
            DisplayLineItems[2] = "NA";
        }

        public int GroupId { get; set; }
        public Dictionary<int, string> DisplayLineItems { get; } = new Dictionary<int, string>();
        public Action UpCallback { get; set; }
        public Action DownCallback { get; set; }
    }
}