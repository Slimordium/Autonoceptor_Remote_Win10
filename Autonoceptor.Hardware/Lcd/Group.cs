using System;
using System.Collections.Generic;

namespace Autonoceptor.Hardware.Lcd
{
    public class Group
    {
        public Group(GroupName groupName)
        {
            DisplayLineItems.Add(1, string.Empty);
            DisplayLineItems.Add(2, string.Empty);

            DisplayLineItems[1] = groupName.ToString();
            DisplayLineItems[2] = "NA";
        }
        public Dictionary<int, string> DisplayLineItems { get; } = new Dictionary<int, string>();

        /// <summary>
        /// Xbox DPad Up
        /// </summary>
        public Action UpCallback { get; set; }

        /// <summary>
        /// Xbox DPad Down
        /// </summary>
        public Action DownCallback { get; set; }
    }
}