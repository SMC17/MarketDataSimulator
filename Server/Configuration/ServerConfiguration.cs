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

        /// <summary>
        /// Per-client connect/subscribe logging. Load runs disable it: console writes serialise on a
        /// global lock and would otherwise dominate the measurement at high subscriber counts.
        /// </summary>
        public bool VerboseLogging { get; set; } = true;

        /// <summary>
        /// When set, the dissemination counters are written to stdout on this cadence so a run can
        /// be reconciled against what the subscribers actually observed. Zero disables reporting.
        /// </summary>
        public double StatisticsIntervalSeconds { get; set; } = 0;

        /// <summary>Stops the process this long after start. Zero runs until killed.</summary>
        public double RunForSeconds { get; set; } = 0;

        /// <summary>
        /// Outbound updates buffered per subscriber before the server starts dropping and forces
        /// that subscriber to resynchronise from the next snapshot. Bounds the blast radius of one
        /// slow consumer.
        /// </summary>
        public int SubscriberQueueCapacity { get; set; } = 1024;

        public static ServerConfiguration FromAppSettings() => FromJson("appsettings.json");
        public static ServerConfiguration FromJson(string filename)
        {
            return JsonSerializer.Deserialize<ServerConfiguration>(File.ReadAllText(filename));
        }
    }
}
