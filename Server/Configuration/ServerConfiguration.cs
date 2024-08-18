using MarketData.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace MarketData.Server.Configuration
{
    public class ServerConfiguration
    {
        public int Port { get; set; }
        public IReadOnlyList<Instrument> Instruments { get; set; }
        public static ServerConfiguration FromAppSettings() => FromJson("appsettings.json");
        public static ServerConfiguration FromJson(string filename)
        {
            return JsonSerializer.Deserialize<ServerConfiguration>(File.ReadAllText(filename));
        }
    }
}
