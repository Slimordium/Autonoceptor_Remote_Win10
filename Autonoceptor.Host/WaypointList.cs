using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Autonoceptor.Shared.Gps;
using Autonoceptor.Shared.Utilities;
using Newtonsoft.Json;
using NLog;


namespace Autonoceptor.Host
{
    public class WaypointList : List<GpsFixData>
    {
        // private static string _filename = $"{Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)}//waypointdata.json";
        private static string _filename = $"waypointdata.json";

        public async Task<bool> Save()
        {
            try
            {
                await Host.FileExtensions.SaveStringToFile(_filename, JsonConvert.SerializeObject(this));
                return true;
            }
            catch (Exception e)
            {
                Console.WriteLine("I had a problem saving the waypoints. ");
                return false;
            }
            
           
        }

        public async Task<WaypointList> Load()
        {
            try
            {
                WaypointList newlist = JsonConvert.DeserializeObject<WaypointList>(await Host.FileExtensions.ReadStringFromFile(_filename));
                return newlist;
            }
            catch (Exception e)
            {
                Console.WriteLine("Could not load the waypoint list. ");
                return new WaypointList();
                // Also tasty
            }
            
        }
        

    }
}
