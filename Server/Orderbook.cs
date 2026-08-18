using MarketData.Common;
using MarketData.Common.Server;
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MarketData.Server
{
    internal class Orderbook : IDisposable
    {
        public Orderbook(Instrument instrument, IOrderbookService service)
        {
            _instrument = instrument;

            _spinTask = GenerateUpdatesAsync(new WeakReference<Orderbook>(this), instrument, service, _disposedSource);
        }

        /// <summary>
        /// Tick on which the generator wakes. Task.Delay cannot resolve sub-millisecond waits, so
        /// the generator wakes on a fixed cadence and emits however many updates fell due, rather
        /// than sleeping once per update. Update rates below 1/tick stay evenly spaced.
        /// </summary>
        private static readonly TimeSpan _tick = TimeSpan.FromMilliseconds(1);

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
                            await PublishUpdateAsync(model, random, instrument, service).ConfigureAwait(false);
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

        private static ValueTask PublishUpdateAsync(Orderbook model, Random random, Instrument instrument, IOrderbookService service)
        {
            OrderbookUpdate update = null;

            if (random.NextDouble() < instrument.Specifications.SnapshotProbability)
            {
                var snapshot = model.Refresh(random, instrument);
                update = new OrderbookUpdate(
                    new OrderbookSnapshotUpdate(instrument.Id, snapshot.Bids, snapshot.Asks));
            }
            else
            {
                var incremental = model.Update(random, instrument);
                update = new OrderbookUpdate(
                    new OrderbookIncrementalUpdate(instrument.Id, incremental.UpdateType, incremental.Level));
            }

            // Stamped as late as possible before the update enters the dissemination path, so the
            // subscriber-side delta measures queueing, fan-out and transport rather than generation.
            update = update with { SourceTimestamp = Stopwatch.GetTimestamp() };

            return service.OnOrderbookUpdateAsync(update);
        }

        private static readonly TimeSpan _lagResetThreshold = TimeSpan.FromMilliseconds(250);

        private OrderbookLevelUpdate Update(Random random, Instrument instrument)
        {
            lock (_disposedLock)
            {
                if (_disposed)
                    return OrderbookLevelUpdate.Empty;
            }

            uint GetQuantity() => (uint)random.Next(1, 1000);
            int GetPrice() => random.Next(-100, 100);

            List<OrderbookLevel> levels = null;
            SortedSet<OrderbookLevel> oppositeSortedLevels = null;
            SortedSet<OrderbookLevel> sortedLevels = null;

            var buy = random.Next(0, 2) == 0;

            levels = buy ? _bidLevels : _askLevels;
            oppositeSortedLevels = buy ? _asks : _bids;
            sortedLevels = buy ? _bids : _asks;

            var replace = levels.Count == instrument.Specifications.Depth;
            var remove = levels.Count > 0
                && random.NextDouble() < (levels.Count / (double)(instrument.Specifications.Depth + 1));

            // The index is only meaningful for Remove/Replace, and Random.Next uses an exclusive
            // upper bound, so it must be sampled from [0, Count) rather than [0, Count - 1).
            if (remove)
            {
                return RemoveLevel(random.Next(0, levels.Count), levels, sortedLevels);
            }
            else if (replace)
            {
                return ReplaceLevel(random.Next(0, levels.Count), levels, sortedLevels, GetQuantity());
            }
            else
            {
                return AddLevel(levels, oppositeSortedLevels, sortedLevels, buy, GetPrice(), GetQuantity());
            }    
        }

        private static OrderbookLevelUpdate AddLevel(List<OrderbookLevel> levels,
            SortedSet<OrderbookLevel> oppositeSortedLevels,
            SortedSet<OrderbookLevel> sortedLevels,
            bool buy, int price, uint quantity)
        {
            OrderbookLevel level = null;

            if (!levels.Any())
            {
                if (!oppositeSortedLevels.Any())
                {
                    level = new OrderbookLevel(price, buy, quantity);
                }
                else
                {
                    // Both comparers order worst-price-first, so Max is the far side's touch on
                    // either side. Seeding an ask from Min (the *worst* bid) crossed the book.
                    var best = oppositeSortedLevels.Max;
                    level = new OrderbookLevel(buy ? best.Price - 1 : best.Price + 1, buy, quantity);
                }
            }
            else
            {
                var minimum = sortedLevels.Min;
                level = new OrderbookLevel(buy ? minimum.Price - 1 : minimum.Price + 1, buy, quantity);
            }

            levels.Add(level);
            sortedLevels.Add(level);

            return new OrderbookLevelUpdate(OrderbookUpdateType.Add, level);
        }

        private static OrderbookLevelUpdate ReplaceLevel(int index, List<OrderbookLevel> levels, SortedSet<OrderbookLevel> sortedLevels, uint quantity)
        {
            var level = levels[index];
            sortedLevels.Remove(level);
            level = level with { Quantity = quantity };
            levels[index] = level;
            sortedLevels.Add(level);
            return new OrderbookLevelUpdate(OrderbookUpdateType.Replace, level);
        }
        private static OrderbookLevelUpdate RemoveLevel(int index, List<OrderbookLevel> levels, SortedSet<OrderbookLevel> sortedLevels)
        {
            var level = levels[index];
            levels.RemoveAt(index);
            sortedLevels.Remove(level);
            return new OrderbookLevelUpdate(OrderbookUpdateType.Remove, level);
        }

        private OrderbookSnapshotUpdate Refresh(Random random, Instrument instrument)
        {
            lock (_disposedLock)
            {
                if (_disposed)
                    return OrderbookSnapshotUpdate.Empty;
            }

            _bids.Clear();
            _bidLevels.Clear();
            _asks.Clear();
            _askLevels.Clear();

            if (random.NextDouble() > 0.99)
                return OrderbookSnapshotUpdate.Empty;

            foreach (var i in Enumerable.Range(0 ,instrument.Specifications.Depth))
                _ = Update(random, instrument);

            return new OrderbookSnapshotUpdate(_instrument.Id, _bidLevels.AsReadOnly(), _askLevels.AsReadOnly());
        }

        public OrderbookSnapshotUpdate GetSnapshot()
        {
            lock (_disposedLock)
            {
                if (_disposed)
                    return OrderbookSnapshotUpdate.Empty;

                return new OrderbookSnapshotUpdate(_instrument.Id, _bidLevels.AsReadOnly(), _askLevels.AsReadOnly());
            }
        }

        public void Dispose()
        {
            lock (_disposedLock)
            {
                if (_disposed)
                    return;

                _disposed = true;

                _disposedSource.TrySetResult();
                _spinTask.GetAwaiter().GetResult();
            }
        }


        private readonly Instrument _instrument = null;
        private readonly List<OrderbookLevel> _bidLevels = new List<OrderbookLevel>();
        private readonly List<OrderbookLevel> _askLevels = new List<OrderbookLevel>();
        private readonly SortedSet<OrderbookLevel> _bids = new SortedSet<OrderbookLevel>(OrderbookLevelComparer.BidComparer);
        private readonly SortedSet<OrderbookLevel> _asks = new SortedSet<OrderbookLevel>(OrderbookLevelComparer.AskComparer);
        private readonly Task _spinTask = null;
        private readonly object _disposedLock = new object();
        private bool _disposed;
        private readonly TaskCompletionSource _disposedSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
