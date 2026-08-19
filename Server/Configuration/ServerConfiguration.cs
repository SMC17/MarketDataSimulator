using MarketData.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace MarketData.Server.Configuration
{
    public class MulticastConfiguration
    {
        public bool Enabled { get; set; }

        /// <summary>Administratively scoped group (239.0.0.0/8), which never leaves the local domain.</summary>
        public string Group { get; set; } = "239.7.7.7";
        public int Port { get; set; } = 31007;

        /// <summary>Interface to publish on. Loopback keeps benchmark traffic off any real network.</summary>
        public string Interface { get; set; } = "127.0.0.1";

        /// <summary>
        /// Messages packed into one datagram. Batching amortises the syscall and the IP/UDP
        /// headers over many updates; 1 sends every update immediately, which is the setting the
        /// latency comparison against unicast is run under.
        /// </summary>
        public int MaxBatch { get; set; } = 1;

        /// <summary>Deadline for flushing a partial batch. Zero flushes on every update.</summary>
        public double FlushIntervalMs { get; set; }

        /// <summary>
        /// How often every book is republished in full. This bounds how long a subscriber that
        /// detected a gap stays dark, since it cannot request a retransmission.
        /// </summary>
        public double SnapshotIntervalSeconds { get; set; } = 1.0;
    }

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

        /// <summary>
        /// Which order book implementation the matching engine runs on: SortedArray, Ladder or
        /// Tree. They are behaviourally identical (held so by differential tests) and differ only
        /// in performance; see BENCHMARKS.md for the measurements behind the default.
        /// </summary>
        public string BookImplementation { get; set; } = MarketData.Common.Books.BookFactory.Default;

        /// <summary>
        /// Prices are confined to [-PriceBand, PriceBand]. The ladder implementation requires a
        /// bounded band; the others simply never see a price outside it.
        /// </summary>
        public int PriceBand { get; set; } = 512;

        /// <summary>Multicast dissemination settings. Disabled by default; unicast gRPC is used instead.</summary>
        public MulticastConfiguration Multicast { get; set; } = new MulticastConfiguration();

        public static ServerConfiguration FromAppSettings() => FromJson("appsettings.json");
        public static ServerConfiguration FromJson(string filename)
        {
            return JsonSerializer.Deserialize<ServerConfiguration>(File.ReadAllText(filename));
        }
    }
}
