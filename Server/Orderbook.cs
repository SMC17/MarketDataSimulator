using MarketData.Common;
using MarketData.Common.Books;
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
        public Orderbook(Instrument instrument, IOrderbookService service, string bookImplementation, int priceBand)
        {
            _instrument = instrument;
            _simulator = new BookSimulator(
                BookFactory.Create(bookImplementation, instrument.Specifications.Depth, priceBand), priceBand);

            _spinTask = GenerateUpdatesAsync(new WeakReference<Orderbook>(this), instrument, service, _disposedSource);
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
            IOrderbookService service,
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
                            await model.PublishUpdateAsync(random, service).ConfigureAwait(false);
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

        private ValueTask PublishUpdateAsync(Random random, IOrderbookService service)
        {
            OrderbookUpdate update;

            lock (_lock)
            {
                if (_disposed)
                    return ValueTask.CompletedTask;

                if (random.NextDouble() < _instrument.Specifications.SnapshotProbability)
                {
                    _simulator.Refresh(random);
                    update = new OrderbookUpdate(BuildSnapshot());
                }
                else
                {
                    var mutation = _simulator.Mutate(random);

                    if (mutation.Kind == MutationKind.None)
                        return ValueTask.CompletedTask;

                    update = new OrderbookUpdate(new OrderbookIncrementalUpdate(
                        _instrument.Id, ToUpdateType(mutation.Kind), ToLevel(mutation.Side, mutation.Level)));
                }
            }

            // Stamped as late as possible before the update enters the dissemination path, so the
            // subscriber-side delta measures queueing, fan-out and transport rather than generation.
            update = update with { SourceTimestamp = Stopwatch.GetTimestamp() };

            return service.OnOrderbookUpdateAsync(update);
        }

        private OrderbookSnapshotUpdate BuildSnapshot()
            => new OrderbookSnapshotUpdate(_instrument.Id, ReadSide(Side.Bid), ReadSide(Side.Ask));

        private IReadOnlyList<OrderbookLevel> ReadSide(Side side)
        {
            var levels = _simulator.ReadSide(side);
            var converted = new List<OrderbookLevel>(levels.Count);

            foreach (var level in levels)
                converted.Add(ToLevel(side, level));

            return converted.AsReadOnly();
        }

        private static OrderbookLevel ToLevel(Side side, PriceLevel level)
            => new OrderbookLevel(level.Price, side == Side.Bid, level.Quantity);

        private static OrderbookUpdateType ToUpdateType(MutationKind kind) => kind switch
        {
            MutationKind.Add => OrderbookUpdateType.Add,
            MutationKind.Replace => OrderbookUpdateType.Replace,
            MutationKind.Remove => OrderbookUpdateType.Remove,
            _ => OrderbookUpdateType.Invalid,
        };

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

        private readonly Instrument _instrument;
        private readonly BookSimulator _simulator;
        private readonly Task _spinTask;
        private readonly object _lock = new object();
        private bool _disposed;
        private readonly TaskCompletionSource _disposedSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
