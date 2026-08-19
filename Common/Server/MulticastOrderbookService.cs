using MarketData.Common.Books;
using MarketData.Common.Feed;
using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace MarketData.Common.Server
{
    /// <summary>
    /// Disseminates the feed over multicast instead of per-subscriber unicast streams.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Structurally the whole point: <see cref="OnOrderbookUpdateAsync"/> performs one encode and
    /// at most one <c>send</c>, and contains no reference to subscribers at all. The unicast
    /// implementation it replaces walks the subscriber table on every update, so its per-update
    /// cost - and the latency spread across that population - grows with the number of listeners.
    /// Here the server genuinely does not know how many there are.
    /// </para>
    /// <para>
    /// Because there is no per-subscriber state, there is also no per-subscriber queue, no slow
    /// consumer to detect and no backpressure to apply. A subscriber that falls behind loses
    /// packets and is responsible for noticing; the periodic snapshot below is what lets it
    /// recover.
    /// </para>
    /// </remarks>
    public sealed class MulticastOrderbookService : IOrderbookService
    {
        public MulticastOrderbookService(IPAddress group, int port, IPAddress @interface,
            int maxBatch, TimeSpan flushInterval, TimeSpan snapshotInterval, IOrderbookManager manager)
        {
            _publisher = new MulticastPublisher(group, port, @interface, maxBatch);
            _flushInterval = flushInterval;
            _snapshotInterval = snapshotInterval;
            _manager = manager;
        }

        public Task StartAsync()
        {
            if (_pump is not null)
                return Task.CompletedTask;

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

            // With batching disabled the packet leaves immediately, which is the configuration the
            // latency comparison against unicast is run in.
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

        /// <summary>
        /// Flushes partial batches on a deadline and republishes full books periodically.
        /// </summary>
        /// <remarks>
        /// The recurring snapshot is the entire recovery story on an unreliable transport. A
        /// subscriber that detects a gap has no way to request a retransmission, so its only route
        /// back to a correct book is to wait for the next complete one; the interval therefore
        /// bounds how long a gapped subscriber stays dark.
        /// </remarks>
        private async Task PumpAsync(CancellationToken token)
        {
            var lastSnapshot = DateTime.UtcNow;

            while (!token.IsCancellationRequested)
            {
                try
                {
                    var delay = _flushInterval > TimeSpan.Zero ? _flushInterval : TimeSpan.FromMilliseconds(1);
                    await Task.Delay(delay, token).ConfigureAwait(false);

                    if (_flushInterval > TimeSpan.Zero)
                        _publisher.Flush();

                    if (_snapshotInterval > TimeSpan.Zero && DateTime.UtcNow - lastSnapshot >= _snapshotInterval)
                    {
                        lastSnapshot = DateTime.UtcNow;
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

        public OrderbookServiceStatistics GetStatistics()
            => new OrderbookServiceStatistics(
                ConnectedClients: 0,            // unknowable by design: the server has no subscriber table
                QueuedUpdates: 0,
                PeakQueuedUpdates: 0,
                PublishedUpdates: Interlocked.Read(ref _published),
                DisseminatedUpdates: _publisher.MessagesSent,
                SentMessages: _publisher.PacketsSent,
                DroppedUpdates: 0,
                FailedSends: 0,
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

            _publisher.Dispose();
            _shutdown.Dispose();
        }

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
        private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();
        private PriceLevel[] _bidScratch = new PriceLevel[64];
        private PriceLevel[] _askScratch = new PriceLevel[64];
        private Task _pump;
        private long _published;
    }
}
