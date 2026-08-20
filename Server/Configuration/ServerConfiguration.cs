using System.Net;
using System.Text.Json;
using MarketData.Common;
using MarketData.Common.Feed;
using MarketData.Common.Durability;

namespace MarketData.Server.Configuration
{
    public sealed class MulticastConfiguration
    {
        public bool Enabled { get; set; }
        public string Group { get; set; } = "239.7.7.7";
        public int Port { get; set; } = 31007;
        public string RedundantGroup { get; set; } = "";
        public int RedundantPort { get; set; }
        public string Interface { get; set; } = "127.0.0.1";
        public int MaxBatch { get; set; } = 1;
        public double FlushIntervalMs { get; set; }
        public double SnapshotIntervalSeconds { get; set; } = 1.0;
        public JournalConfiguration Journal { get; set; } = new JournalConfiguration();
    }

    public sealed class JournalConfiguration
    {
        public bool Enabled { get; set; }
        public string Directory { get; set; } = "journal";
        public string Policy { get; set; } = nameof(DurabilityPolicy.SyncPeriodic);
        public long SegmentBytes { get; set; } = 64L * 1024 * 1024;
        public double SyncIntervalMs { get; set; } = 200;
        public int RetransmissionPort { get; set; }
    }

    /// <summary>
    /// The delivery objective the server measures itself against while running.
    /// </summary>
    /// <remarks>
    /// Stated over an explicit window, because an availability target without one is not a
    /// commitment: the same percentage means an hour a year or four minutes a day depending on
    /// what it is measured over.
    /// </remarks>
    public sealed class ServiceLevelConfiguration
    {
        public bool Enabled { get; set; } = true;

        /// <summary>Share of published updates that must reach subscribers.</summary>
        public double DeliveryObjective { get; set; } = 0.999;

        public double WindowSeconds { get; set; } = 3600;
    }

    public sealed class ServerConfiguration
    {
        public ServiceLevelConfiguration ServiceLevel { get; set; } = new ServiceLevelConfiguration();
        public int Port { get; set; } = 14000;
        public IReadOnlyList<Instrument> Instruments { get; set; } = Array.Empty<Instrument>();
        public bool VerboseLogging { get; set; } = true;
        public double StatisticsIntervalSeconds { get; set; }
        public double RunForSeconds { get; set; }
        public int SubscriberQueueCapacity { get; set; } = 1024;
        public int PriceBand { get; set; } = 512;
        public int Seed { get; set; } = 20260819;
        public MulticastConfiguration Multicast { get; set; } = new MulticastConfiguration();
        public bool UseRingQueue { get; set; }

        public static ServerConfiguration FromAppSettings() => FromJson("appsettings.json");

        public static ServerConfiguration FromJson(string filename)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filename);
            var config = JsonSerializer.Deserialize<ServerConfiguration>(File.ReadAllText(filename))
                ?? throw new InvalidDataException("configuration is empty");
            config.Validate();
            return config;
        }

        public void Validate()
        {
            ValidatePort(Port, nameof(Port));

            if (SubscriberQueueCapacity is < 1 or > 1_048_576)
                throw new InvalidDataException($"{nameof(SubscriberQueueCapacity)} must be in [1, 1048576]");
            if (PriceBand is < 1 or > 1_000_000)
                throw new InvalidDataException($"{nameof(PriceBand)} must be in [1, 1000000]");
            if (!IsNonNegativeFinite(StatisticsIntervalSeconds))
                throw new InvalidDataException($"{nameof(StatisticsIntervalSeconds)} must be finite and non-negative");
            if (!IsNonNegativeFinite(RunForSeconds))
                throw new InvalidDataException($"{nameof(RunForSeconds)} must be finite and non-negative");
            if (StatisticsIntervalSeconds * 1_000 > MaxTimerMilliseconds ||
                RunForSeconds * 1_000 > MaxTimerMilliseconds)
                throw new InvalidDataException("timer intervals exceed the runtime timer limit");
            if (ServiceLevel is not null && ServiceLevel.Enabled)
            {
                if (!double.IsFinite(ServiceLevel.DeliveryObjective) ||
                    ServiceLevel.DeliveryObjective <= 0 || ServiceLevel.DeliveryObjective > 1)
                {
                    throw new InvalidDataException(
                        $"{nameof(ServiceLevel)}.{nameof(ServiceLevelConfiguration.DeliveryObjective)} " +
                        "must be in (0, 1]");
                }

                if (!double.IsFinite(ServiceLevel.WindowSeconds) || ServiceLevel.WindowSeconds <= 0)
                {
                    throw new InvalidDataException(
                        $"{nameof(ServiceLevel)}.{nameof(ServiceLevelConfiguration.WindowSeconds)} " +
                        "must be positive and finite");
                }
            }

            if (Instruments is null || Instruments.Count == 0)
                throw new InvalidDataException("at least one instrument is required");

            var ids = new HashSet<int>();
            var symbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var instrument in Instruments)
            {
                if (instrument is null)
                    throw new InvalidDataException("instrument entries cannot be null");
                if (instrument.Id <= 0 || !ids.Add(instrument.Id))
                    throw new InvalidDataException($"instrument id {instrument.Id} must be positive and unique");
                if (string.IsNullOrWhiteSpace(instrument.Symbol))
                    throw new InvalidDataException($"instrument {instrument.Id} requires a symbol");
                if (!symbols.Add(instrument.Symbol.Trim()))
                    throw new InvalidDataException($"instrument symbol {instrument.Symbol} must be unique");
                if (instrument.Specifications is null)
                    throw new InvalidDataException($"instrument {instrument.Id} requires specifications");

                var spec = instrument.Specifications;

                if (spec.Depth < 1 || spec.Depth > FeedProtocol.MaxSnapshotLevels / 2)
                    throw new InvalidDataException(
                        $"instrument {instrument.Id} depth must fit one {FeedProtocol.MaxPacketSize}-byte snapshot");
                if (!double.IsFinite(spec.UpdatesPerSecond) ||
                    spec.UpdatesPerSecond is <= 0 or > 10_000_000)
                    throw new InvalidDataException(
                        $"instrument {instrument.Id} update rate must be in (0, 10000000]");
                if (!double.IsFinite(spec.SnapshotProbability) || spec.SnapshotProbability is < 0 or > 1)
                    throw new InvalidDataException($"instrument {instrument.Id} snapshot probability must be in [0, 1]");
            }

            Multicast ??= new MulticastConfiguration();

            if (!Multicast.Enabled)
                return;

            ValidatePort(Multicast.Port, "Multicast.Port");

            if (!TryParseMulticast(Multicast.Group, out var group))
                throw new InvalidDataException("Multicast.Group must be an IPv4 multicast address");
            if (!IPAddress.TryParse(Multicast.Interface, out var adapter) || adapter.AddressFamily !=
                System.Net.Sockets.AddressFamily.InterNetwork)
                throw new InvalidDataException("Multicast.Interface must be an IPv4 address");
            if (Multicast.MaxBatch is < 1 or > ushort.MaxValue)
                throw new InvalidDataException("Multicast.MaxBatch must be in [1, 65535]");
            if (!IsNonNegativeFinite(Multicast.FlushIntervalMs))
                throw new InvalidDataException("Multicast.FlushIntervalMs must be finite and non-negative");
            if (Multicast.FlushIntervalMs > MaxTimerMilliseconds)
                throw new InvalidDataException("Multicast.FlushIntervalMs exceeds the runtime timer limit");
            if (!double.IsFinite(Multicast.SnapshotIntervalSeconds) || Multicast.SnapshotIntervalSeconds <= 0)
                throw new InvalidDataException("Multicast.SnapshotIntervalSeconds must be finite and positive");
            if (Multicast.SnapshotIntervalSeconds > TimeSpan.MaxValue.TotalSeconds)
                throw new InvalidDataException("Multicast.SnapshotIntervalSeconds exceeds TimeSpan capacity");

            Multicast.Journal ??= new JournalConfiguration();
            ValidateJournal(Multicast.Journal);

            if (string.IsNullOrWhiteSpace(Multicast.RedundantGroup))
            {
                if (Multicast.RedundantPort != 0)
                    throw new InvalidDataException("Multicast.RedundantPort requires RedundantGroup");

                return;
            }

            if (!TryParseMulticast(Multicast.RedundantGroup, out var redundantGroup))
                throw new InvalidDataException("Multicast.RedundantGroup must be an IPv4 multicast address");

            var redundantPort = Multicast.RedundantPort == 0 ? Multicast.Port : Multicast.RedundantPort;
            ValidatePort(redundantPort, "Multicast.RedundantPort");

            if (group.Equals(redundantGroup) && Multicast.Port == redundantPort)
                throw new InvalidDataException("multicast A and B endpoints must differ");
        }

        private static void ValidateJournal(JournalConfiguration journal)
        {
            if (!journal.Enabled)
            {
                if (journal.RetransmissionPort != 0)
                    throw new InvalidDataException(
                        "Multicast.Journal.RetransmissionPort requires the journal");
                return;
            }

            if (string.IsNullOrWhiteSpace(journal.Directory))
                throw new InvalidDataException("Multicast.Journal.Directory is required");
            try { _ = Path.GetFullPath(journal.Directory); }
            catch (Exception error) when (error is ArgumentException or NotSupportedException)
            {
                throw new InvalidDataException("Multicast.Journal.Directory is invalid", error);
            }

            if (string.IsNullOrWhiteSpace(journal.Policy) ||
                int.TryParse(journal.Policy, out _) ||
                !Enum.TryParse<DurabilityPolicy>(journal.Policy, true, out var policy) ||
                !Enum.IsDefined(policy))
                throw new InvalidDataException("Multicast.Journal.Policy is invalid");

            var minimumSegment = JournalRecord.SizeFor(16) +
                JournalRecord.SizeFor(JournalRecord.MaxPayloadSize);
            if (journal.SegmentBytes < minimumSegment)
                throw new InvalidDataException("Multicast.Journal.SegmentBytes is too small");
            if (!double.IsFinite(journal.SyncIntervalMs) || journal.SyncIntervalMs <= 0 ||
                journal.SyncIntervalMs > uint.MaxValue - 1)
                throw new InvalidDataException("Multicast.Journal.SyncIntervalMs is invalid");
            if (journal.RetransmissionPort != 0)
                ValidatePort(journal.RetransmissionPort, "Multicast.Journal.RetransmissionPort");
        }

        private static bool IsNonNegativeFinite(double value) => double.IsFinite(value) && value >= 0;
        private const double MaxTimerMilliseconds = 4_294_967_294;

        private static bool TryParseMulticast(string value, out IPAddress address)
            => IPAddress.TryParse(value, out address) && address.AddressFamily ==
                System.Net.Sockets.AddressFamily.InterNetwork &&
                address.GetAddressBytes()[0] is >= 224 and <= 239;

        private static void ValidatePort(int value, string name)
        {
            if (value is < 1 or > 65535)
                throw new InvalidDataException($"{name} must be in [1, 65535]");
        }
    }
}
