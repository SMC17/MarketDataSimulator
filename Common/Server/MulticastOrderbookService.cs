using MarketData.Common.Books;
using MarketData.Common.Feed;
using System;
using System.Diagnostics;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using MarketData.Common.Durability;

namespace MarketData.Common.Server
{
    /// <summary>Subscriber-independent multicast dissemination with optional durable gap fill.</summary>
    public sealed class MulticastOrderbookService : IOrderbookService
    {
        public MulticastOrderbookService(IPAddress group, int port, IPAddress @interface,
            int maxBatch, TimeSpan flushInterval, TimeSpan snapshotInterval, IOrderbookManager manager,
            IPAddress redundantGroup = null, int redundantPort = 0,
            WriteAheadJournal journal = null, int retransmissionPort = 0)
        {
            _publisher = new MulticastPublisher(group, port, @interface, maxBatch,
                redundantGroup, redundantPort, journal?.SessionId ?? 0, journal);
            _flushInterval = flushInterval;
            _snapshotInterval = snapshotInterval;
            _manager = manager;
            _journal = journal;

            if (retransmissionPort != 0 && journal is null)
                throw new ArgumentException("Retransmission requires a journal.", nameof(retransmissionPort));
            if (journal is not null && retransmissionPort > 0)
                _retransmission = new RetransmissionService(journalDirectory: JournalDirectory(journal),
                    port: retransmissionPort, address: @interface);
        }

        public Task StartAsync()
        {
            if (_pump is not null)
                return Task.CompletedTask;

            _retransmission?.Start();
            _pump = PumpAsync(_shutdown.Token);
            return Task.CompletedTask;
        }

        public ValueTask OnOrderbookUpdateAsync(OrderbookUpdate update)
        {
            if (update.IsSnapshot)
            {
                PublishSnapshot(update.Snapshot);
            }
            else
            {
                var incremental = update.Incremental;

                _publisher.Publish(ToMessageType(incremental.Type), incremental.InstrumentId,
                    incremental.Level.IsBuy ? Side.Bid : Side.Ask,
                    new PriceLevel(incremental.Level.Price, incremental.Level.Quantity));
            }

            // Zero flush interval disables batching latency.
            if (_flushInterval <= TimeSpan.Zero)
                _publisher.Flush();

            Interlocked.Increment(ref _published);
            return ValueTask.CompletedTask;
        }

        private void PublishSnapshot(OrderbookSnapshotUpdate snapshot)
        {
            var bids = Rent(ref _bidScratch, snapshot.Bids.Count);
            var asks = Rent(ref _askScratch, snapshot.Asks.Count);

            for (var i = 0; i < snapshot.Bids.Count; i++)
                bids[i] = new PriceLevel(snapshot.Bids[i].Price, snapshot.Bids[i].Quantity);

            for (var i = 0; i < snapshot.Asks.Count; i++)
                asks[i] = new PriceLevel(snapshot.Asks[i].Price, snapshot.Asks[i].Quantity);

            _publisher.PublishSnapshot(snapshot.InstrumentId,
                bids.AsSpan(0, snapshot.Bids.Count), asks.AsSpan(0, snapshot.Asks.Count));
        }

        private static PriceLevel[] Rent(ref PriceLevel[] buffer, int required)
        {
            if (buffer.Length < required)
                buffer = new PriceLevel[Math.Max(required, buffer.Length * 2)];

            return buffer;
        }

        /// <summary>Flushes partial batches and republishes recovery snapshots.</summary>
        private async Task PumpAsync(CancellationToken token)
        {
            var lastSnapshot = Stopwatch.GetTimestamp();

            while (!token.IsCancellationRequested)
            {
                try
                {
                    var delay = _flushInterval > TimeSpan.Zero ? _flushInterval : TimeSpan.FromMilliseconds(1);
                    await Task.Delay(delay, token).ConfigureAwait(false);

                    if (_flushInterval > TimeSpan.Zero)
                        _publisher.Flush();

                    if (_snapshotInterval > TimeSpan.Zero &&
                        Stopwatch.GetElapsedTime(lastSnapshot) >= _snapshotInterval)
                    {
                        lastSnapshot = Stopwatch.GetTimestamp();
                        RepublishSnapshots();
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception e)
                {
                    Console.WriteLine($"Error in {nameof(PumpAsync)}: {e}");
                }
            }
        }

        private void RepublishSnapshots()
        {
            foreach (var instrumentId in _manager.InstrumentIds)
            {
                PublishSnapshot(_manager.GetSnapshot(instrumentId));
                Interlocked.Increment(ref _published);
            }

            _publisher.Flush();
        }

        /// <summary>Publishing is a single send under a lock, so producers share one handle.</summary>
        public IUpdateProducer RegisterProducer() => new DirectProducer(this);

        private sealed class DirectProducer : IUpdateProducer
        {
            public DirectProducer(MulticastOrderbookService service) => _service = service;

            public ValueTask PublishAsync(OrderbookUpdate update) => _service.OnOrderbookUpdateAsync(update);

            private readonly MulticastOrderbookService _service;
        }

        public OrderbookServiceStatistics GetStatistics()
            => new OrderbookServiceStatistics(
                ConnectedClients: 0,            // unknowable by design: the server has no subscriber table
                QueuedUpdates: 0,
                PeakQueuedUpdates: 0,
                PublishedUpdates: Interlocked.Read(ref _published),
                DisseminatedUpdates: _publisher.MessagesSent,
                SentMessages: _publisher.PacketsSent,
                DroppedUpdates: 0,
                FailedSends: _publisher.SendFailures + _publisher.JournalFailures,
                OutboundQueued: 0,
                MaxOutboundQueued: 0);

        public Task StopAsync()
        {
            _shutdown.Cancel();
            return _pump ?? Task.CompletedTask;
        }

        public void Dispose()
        {
            try
            {
                StopAsync().GetAwaiter().GetResult();
            }
            catch (Exception)
            {
                // Shutting down; the pump's cancellation is expected.
            }

            _retransmission?.Dispose();

            try
            {
                _publisher.Dispose();
            }
            finally
            {
                _journal?.Dispose();
                _shutdown.Dispose();
            }
        }

        private static string JournalDirectory(WriteAheadJournal journal)
            => journal.DirectoryPath;

        private static FeedMessageType ToMessageType(OrderbookUpdateType type) => type switch
        {
            OrderbookUpdateType.Add => FeedMessageType.Add,
            OrderbookUpdateType.Replace => FeedMessageType.Replace,
            OrderbookUpdateType.Remove => FeedMessageType.Remove,
            _ => FeedMessageType.Invalid,
        };

        private readonly MulticastPublisher _publisher;
        private readonly TimeSpan _flushInterval;
        private readonly TimeSpan _snapshotInterval;
        private readonly IOrderbookManager _manager;
        private readonly WriteAheadJournal _journal;
        private readonly RetransmissionService _retransmission;
        private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();
        private PriceLevel[] _bidScratch = new PriceLevel[64];
        private PriceLevel[] _askScratch = new PriceLevel[64];
        private Task _pump;
        private long _published;
    }
}
