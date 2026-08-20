using MarketData.Common;
using MarketData.Common.Server;
using MarketData.Server;
using MarketData.Server.Configuration;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MarketData.Common.Availability;
using MarketData.Common.Durability;
using MarketData.Common.Feed;
using MarketData.Common.Time;

namespace MarketData.Server
{
    internal class Server : IOrderbookManager, IDisposable
    {
        public Server(ServerConfiguration config)
        {
            _config = config;
            _service = CreateService(config);

            foreach (var instrument in config.Instruments)
                _orderbooks.Add(instrument.Id,
                    new Orderbook(instrument, _service.RegisterProducer(), config.PriceBand, config.Seed));
        }

        /// <summary>Selects gRPC fan-out or multicast without changing the matching path.</summary>
        private IOrderbookService CreateService(ServerConfiguration config)
        {
            if (!config.Multicast.Enabled)
                return new OrderbookService(config.Port, this, config.VerboseLogging,
                    config.SubscriberQueueCapacity, config.UseRingQueue);

            var journal = CreateJournal(config.Multicast.Journal);

            try
            {
                return new MulticastOrderbookService(
                    IPAddress.Parse(config.Multicast.Group),
                    config.Multicast.Port,
                    IPAddress.Parse(config.Multicast.Interface),
                    config.Multicast.MaxBatch,
                    TimeSpan.FromMilliseconds(config.Multicast.FlushIntervalMs),
                    TimeSpan.FromSeconds(config.Multicast.SnapshotIntervalSeconds),
                    this,
                    string.IsNullOrWhiteSpace(config.Multicast.RedundantGroup)
                        ? null
                        : IPAddress.Parse(config.Multicast.RedundantGroup),
                    config.Multicast.RedundantPort,
                    journal,
                    config.Multicast.Journal.RetransmissionPort);
            }
            catch
            {
                journal?.Dispose();
                throw;
            }
        }

        private static WriteAheadJournal CreateJournal(JournalConfiguration configuration)
        {
            if (!configuration.Enabled)
                return null;

            var sessionId = MulticastPublisher.NewSessionId();
            var directory = Path.Combine(Path.GetFullPath(configuration.Directory),
                $"session-{sessionId:X16}");
            var policy = Enum.Parse<DurabilityPolicy>(configuration.Policy, ignoreCase: true);

            return new WriteAheadJournal(directory, sessionId, policy,
                configuration.SegmentBytes, TimeSpan.FromMilliseconds(configuration.SyncIntervalMs),
                initialSequence: 0);
        }

        public IReadOnlyCollection<int> InstrumentIds => _orderbooks.Keys;

        /// <summary>
        /// Records what this host can actually measure and control, once, at start-up.
        /// </summary>
        /// <remarks>
        /// Printed rather than assumed because every latency figure this process emits is bounded
        /// by the clock that produced it, and every placement claim is bounded by what the cgroup
        /// permits. A run whose log does not say which host it was on is a run whose numbers cannot
        /// be compared with any other.
        /// </remarks>
        private static void ReportEnvironment()
        {
            var clock = UncertainClock.Detect();
            var placement = ProcessorPlacement.Detect();

            Console.WriteLine(
                $"ENV clockSource={clock.Source} clockUncertainty={clock.UncertaintyNanoseconds}ns " +
                $"processors={placement.LogicalProcessors} allowed={placement.AllowedProcessors.Count} " +
                $"numaNodes={placement.NumaNodes} canPin={placement.CanPinThreads}");

            Console.WriteLine($"ENV note: {placement.Notes}");
            Console.WriteLine(
                "ENV note: cross-host clock agreement is unmeasured here; no PTP grandmaster or " +
                "NIC hardware timestamping is available, so one-way latency across hosts is not " +
                "a claim this process can support.");
        }

        public async Task RunAsync(CancellationToken token)
        {
            ReportEnvironment();

            await _service.StartAsync().ConfigureAwait(false);

            Console.WriteLine(
                (_config.Multicast.Enabled
                    ? $"Publishing to multicast {_config.Multicast.Group}:{_config.Multicast.Port} " +
                      (string.IsNullOrWhiteSpace(_config.Multicast.RedundantGroup)
                          ? ""
                          : $"+ {_config.Multicast.RedundantGroup}:" +
                            $"{(_config.Multicast.RedundantPort == 0 ? _config.Multicast.Port : _config.Multicast.RedundantPort)} ") +
                      $"(batch {_config.Multicast.MaxBatch}, snapshot every {_config.Multicast.SnapshotIntervalSeconds}s)"
                    : $"Listening on {_config.Port} ({(_config.UseRingQueue ? "ring" : "channel")} queue)")
                + " | instruments: " + string.Join(", ",
                _config.Instruments.Select(i =>
                    $"{i.Symbol}(depth {i.Specifications.Depth}, {i.Specifications.UpdatesPerSecond:0.##}/s)"))
                + $" | seed: {_config.Seed}");

            using var lifetime = _config.RunForSeconds > 0
                ? new CancellationTokenSource(TimeSpan.FromSeconds(_config.RunForSeconds))
                : new CancellationTokenSource();

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, token);

            await ReportStatisticsAsync(linked.Token).ConfigureAwait(false);
        }

        private async Task ReportStatisticsAsync(CancellationToken token)
        {
            if (_config.StatisticsIntervalSeconds <= 0)
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }

                return;
            }

            var interval = TimeSpan.FromSeconds(_config.StatisticsIntervalSeconds);
            var previous = _service.GetStatistics();
            var stopwatch = Stopwatch.StartNew();

            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(interval, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                var elapsed = stopwatch.Elapsed.TotalSeconds;
                stopwatch.Restart();

                var current = _service.GetStatistics();

                Console.WriteLine(
                    $"STATS clients={current.ConnectedClients} " +
                    $"queue={current.QueuedUpdates} peakQueue={current.PeakQueuedUpdates} " +
                    $"published/s={(current.PublishedUpdates - previous.PublishedUpdates) / elapsed:0} " +
                    $"disseminated/s={(current.DisseminatedUpdates - previous.DisseminatedUpdates) / elapsed:0} " +
                    $"sent/s={(current.SentMessages - previous.SentMessages) / elapsed:0} " +
                    $"dropped={current.DroppedUpdates} failed={current.FailedSends} " +
                    $"outQueued={current.OutboundQueued} outMax={current.MaxOutboundQueued}");

                ReportServiceLevel(current, stopwatch);

                previous = current;
            }

            var final = _service.GetStatistics();
            Console.WriteLine($"FINAL published={final.PublishedUpdates} disseminated={final.DisseminatedUpdates} sent={final.SentMessages} dropped={final.DroppedUpdates} failed={final.FailedSends}");
        }

        /// <summary>
        /// Emits the delivery objective's budget and burn rate on its own line.
        /// </summary>
        /// <remarks>
        /// A separate line on purpose: the STATS line is parsed by the benchmark harness, and
        /// changing its shape would silently invalidate the recorded transport results. Adding a
        /// field to a format something else is reading is how a measurement record quietly breaks.
        /// <para>
        /// Burn rate rather than a met/missed flag, because "are we meeting it" is answerable yes
        /// or no while telling you nothing about how much room is left. A burn above 1 means the
        /// current failure rate exhausts the budget before the window closes.
        /// </para>
        /// </remarks>
        private void ReportServiceLevel(OrderbookServiceStatistics current, Stopwatch sinceStart)
        {
            var configured = _config.ServiceLevel;

            if (configured is null || !configured.Enabled)
                return;

            _slo ??= new ServiceLevelObjective("delivery", configured.DeliveryObjective,
                TimeSpan.FromSeconds(configured.WindowSeconds));

            // Everything the engine published had to reach somebody; what did not is the failure.
            var total = current.PublishedUpdates;
            var failures = current.DroppedUpdates + current.FailedSends;

            if (total <= 0)
                return;

            var consumed = _slo.BudgetConsumed(total, failures);
            var burn = _slo.BurnRate(total, failures, _uptime.Elapsed);
            var status = new SloStatus(_slo, total, failures, consumed, burn);

            Console.WriteLine(
                $"SLO objective={_slo.Objective:P3} window={_slo.Window.TotalSeconds:0}s " +
                $"published={total} failed={failures} budgetUsed={consumed * 100:0.00}% " +
                $"burn={burn:0.00}x met={status.Met} willBreach={status.WillBreach}");
        }

        public OrderbookSnapshotUpdate GetSnapshot(int instrumentId)
        {
            if (!_orderbooks.TryGetValue(instrumentId, out var orderbook))
                throw new InvalidOperationException($"Orderbook ({instrumentId}) does not exist.");

            return orderbook.GetSnapshot();
        }

        public void Dispose()
        {
            foreach (var orderbook in _orderbooks.Values)
                orderbook.Dispose();

            _service.Dispose();
        }

        private ServiceLevelObjective _slo;
        private readonly Stopwatch _uptime = Stopwatch.StartNew();
        private readonly ServerConfiguration _config = null;
        private readonly IOrderbookService _service = null;
        private readonly Dictionary<int, Orderbook> _orderbooks = new Dictionary<int, Orderbook>();
    }
}
