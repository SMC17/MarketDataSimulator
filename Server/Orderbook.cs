using MarketData.Common;
using MarketData.Common.Books;
using MarketData.Common.Matching;
using MarketData.Common.Server;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace MarketData.Server
{
    /// <summary>
    /// Drives one instrument's book and publishes the resulting updates.
    /// </summary>
    /// <remarks>
    /// Book state lives in an <see cref="IOrderBook"/>; this type only decides what happens next
    /// and turns the result into a wire update. Keeping the two apart is what lets the book
    /// implementations be swapped and compared without touching the simulation, and lets the
    /// simulation be tested without a network.
    /// </remarks>
    internal sealed class Orderbook : IDisposable
    {
        public Orderbook(Instrument instrument, IUpdateProducer producer, string bookImplementation, int priceBand)
        {
            _instrument = instrument;
            _depth = instrument.Specifications.Depth;
            _flow = new OrderFlowSimulator(new LimitOrderBook(-priceBand, priceBand));

            _spinTask = GenerateUpdatesAsync(new WeakReference<Orderbook>(this), instrument, producer, _disposedSource);
        }

        /// <summary>
        /// Tick on which the generator wakes. Task.Delay cannot resolve sub-millisecond waits, so
        /// the generator wakes on a fixed cadence and emits however many updates fell due, rather
        /// than sleeping once per update. Update rates below 1/tick stay evenly spaced.
        /// </summary>
        private static readonly TimeSpan _tick = TimeSpan.FromMilliseconds(1);
        private static readonly TimeSpan _lagResetThreshold = TimeSpan.FromMilliseconds(250);

        private static async Task GenerateUpdatesAsync(WeakReference<Orderbook> orderbook,
            Instrument instrument,
            IUpdateProducer producer,
            TaskCompletionSource disposedSource)
        {
            var random = new Random(instrument.Id * 7919 + DateTime.Now.Millisecond);
            var specifications = instrument.Specifications;

            // System.Text.Json on .NET 6 fills missing constructor parameters with default(T) rather
            // than the declared default, so a config that omits UpdatesPerSecond would otherwise
            // deserialise to a rate of zero and produce a silent feed. Treat non-positive as unset.
            var updatesPerSecond = specifications.UpdatesPerSecond > 0 ? specifications.UpdatesPerSecond : 1.0;
            var updatesPerTick = updatesPerSecond * _tick.TotalSeconds;
            var stopwatch = Stopwatch.StartNew();

            var scheduled = TimeSpan.Zero;
            var due = 0d;

            while (true)
            {
                try
                {
                    scheduled += _tick;
                    var wait = scheduled - stopwatch.Elapsed;

                    if (wait > TimeSpan.Zero)
                        await Task.WhenAny(Task.Delay(wait), disposedSource.Task).ConfigureAwait(false);
                    else if (-wait > _lagResetThreshold)
                        scheduled = stopwatch.Elapsed; // fell far behind; re-anchor instead of stampeding

                    if (disposedSource.Task.IsCompleted)
                        return;

                    if (!orderbook.TryGetTarget(out var model))
                        return;

                    try
                    {
                        due += updatesPerTick;

                        while (due >= 1d)
                        {
                            due -= 1d;
                            await model.PublishUpdateAsync(random, producer).ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        model = null;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to perform orderbook update: {ex}");
                }
            }
        }

        /// <summary>
        /// Advances the market by one action and publishes what changed.
        /// </summary>
        /// <remarks>
        /// The depth feed is derived from the order-by-order events rather than generated
        /// alongside them: whichever price levels the matching engine touched are re-read and
        /// republished. That direction keeps a single source of truth - a subscriber applying the
        /// depth feed necessarily agrees with the engine's book, because the feed is a function of
        /// it.
        /// </remarks>
        private async ValueTask PublishUpdateAsync(Random random, IUpdateProducer producer)
        {
            OrderbookUpdate snapshot = null;
            var updates = _updates;
            updates.Clear();

            lock (_lock)
            {
                if (_disposed)
                    return;

                if (random.NextDouble() < _instrument.Specifications.SnapshotProbability)
                {
                    snapshot = new OrderbookUpdate(BuildSnapshot());
                }
                else
                {
                    _events.Clear();
                    _flow.Step(random, _events);

                    if (_events.Count == 0)
                        return;

                    CollectLevelUpdates(_events, updates);

                    // Cancels and fills continually retire orders the generator still holds ids
                    // for; compacting occasionally keeps that list from growing without bound.
                    if (++_stepsSinceCompact >= CompactInterval)
                    {
                        _stepsSinceCompact = 0;
                        _flow.Compact();
                    }
                }
            }

            var stamp = Stopwatch.GetTimestamp();

            if (snapshot is not null)
            {
                await producer.PublishAsync(snapshot with { SourceTimestamp = stamp }).ConfigureAwait(false);
                return;
            }

            foreach (var update in updates)
                await producer.PublishAsync(update with { SourceTimestamp = stamp }).ConfigureAwait(false);
        }

        /// <summary>Turns the engine's order-level events into aggregated level updates.</summary>
        private void CollectLevelUpdates(List<MarketEvent> events, List<OrderbookUpdate> updates)
        {
            _changes.Clear();
            _projection.Project(_flow.Book, events, _changes);

            foreach (var change in _changes)
            {
                var type = change.IsRemoval ? OrderbookUpdateType.Remove : OrderbookUpdateType.Replace;
                var level = new OrderbookLevel(change.Price, change.Side == Side.Bid,
                    (uint)Math.Min(change.Quantity, uint.MaxValue));

                updates.Add(new OrderbookUpdate(new OrderbookIncrementalUpdate(_instrument.Id, type, level)));
            }
        }

        private OrderbookSnapshotUpdate BuildSnapshot()
            => new OrderbookSnapshotUpdate(_instrument.Id, ReadSide(Side.Bid), ReadSide(Side.Ask));

        private IReadOnlyList<OrderbookLevel> ReadSide(Side side)
        {
            var count = _flow.Book.CopyDepth(side, _scratch.AsSpan(0, _depth));
            var levels = new List<OrderbookLevel>(count);

            for (var i = 0; i < count; i++)
                levels.Add(new OrderbookLevel(_scratch[i].Price, side == Side.Bid, _scratch[i].Quantity));

            return levels.AsReadOnly();
        }

        public OrderbookSnapshotUpdate GetSnapshot()
        {
            lock (_lock)
                return _disposed ? OrderbookSnapshotUpdate.Empty : BuildSnapshot();
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed)
                    return;

                _disposed = true;
            }

            _disposedSource.TrySetResult();
            _spinTask.GetAwaiter().GetResult();
        }

        private const int CompactInterval = 4096;

        private readonly Instrument _instrument;
        private readonly int _depth;
        private readonly OrderFlowSimulator _flow;
        private readonly List<MarketEvent> _events = new List<MarketEvent>(64);
        private readonly List<OrderbookUpdate> _updates = new List<OrderbookUpdate>(64);
        private readonly DepthProjection _projection = new DepthProjection();
        private readonly List<LevelChange> _changes = new List<LevelChange>(64);
        private readonly PriceLevel[] _scratch = new PriceLevel[1024];
        private int _stepsSinceCompact;
        private readonly Task _spinTask;
        private readonly object _lock = new object();
        private bool _disposed;
        private readonly TaskCompletionSource _disposedSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
