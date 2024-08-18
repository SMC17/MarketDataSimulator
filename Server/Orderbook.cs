using MarketData.Common;
using MarketData.Common.Server;
using System;
using System.Collections.Generic;
using System.Data.Common;
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

        private static async Task GenerateUpdatesAsync(WeakReference<Orderbook> orderbook, Instrument instrument, IOrderbookService service, TaskCompletionSource disposedSource)
        {
            var random = new Random(DateTime.Now.Millisecond);

            while (true)
            {
                if (!orderbook.TryGetTarget(out var model))
                    return;

                try
                {
                    await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(random.NextDouble() * 2)), disposedSource.Task).ConfigureAwait(false);

                    if (disposedSource.Task.IsCompleted)
                        return;

                    OrderbookUpdate update = null;

                    if (random.NextDouble() > 0.95)
                    {
                        var snapshot = model.Refresh(random, instrument);
                        update = new OrderbookUpdate(new OrderbookSnapshotUpdate(instrument.Id, snapshot.Bids, snapshot.Asks));
                    }
                    else
                    {
                        var incremental = model.Update(random, instrument);
                        update = new OrderbookUpdate(new OrderbookIncrementalUpdate(instrument.Id, incremental.UpdateType, incremental.Level));
                    }
                    
                    await service.OnOrderbookUpdateAsync(update).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to perform orderbook update: {ex}");
                }
                finally
                {
                    model = null;
                }
            }
        }

        private OrderbookLevelUpdate Update(Random random, Instrument instrument)
        {
            lock (_disposedLock)
            {
                if (_disposed)
                    return OrderbookLevelUpdate.Empty;
            }

            uint GetQuantity() => (uint)random.Next(1, 1000);

            List<OrderbookLevel> levels = null;
            SortedSet<OrderbookLevel> oppositeSortedLevels = null;
            SortedSet<OrderbookLevel> sortedLevels = null;

            var buy = random.Next(0, 2) == 0;

            levels = buy ? _bidLevels : _askLevels;
            oppositeSortedLevels = buy ? _asks : _bids;
            sortedLevels = buy ? _bids : _asks;

            var replace = levels.Count == instrument.Specifications.Depth;
            var remove = random.NextDouble() < (levels.Count / (double)(instrument.Specifications.Depth + 1));

            if (remove)
            {
                var index = random.Next(0, levels.Count - 1);
                var level = levels[index];
                levels.Remove(level);
                sortedLevels.Remove(level);
                return new OrderbookLevelUpdate(OrderbookUpdateType.Remove, level);
            }
            else if (replace)
            {
                var index = random.Next(0, levels.Count - 1);
                var level = levels[index];
                sortedLevels.Remove(level);
                level = level with { Quantity = GetQuantity() };
                levels[index] = level;
                sortedLevels.Add(level);
                return new OrderbookLevelUpdate(OrderbookUpdateType.Replace, level);
            }
            else
            {
                OrderbookLevel level = null;

                if (!levels.Any())
                {
                    if (!oppositeSortedLevels.Any())
                    {
                        level = new OrderbookLevel(random.Next(0, 100), buy, GetQuantity());
                    }
                    else
                    {
                        var minimum = oppositeSortedLevels.Min;
                        var maximum = oppositeSortedLevels.Max;
                        level = new OrderbookLevel(buy ? maximum.Price - 1 : minimum.Price + 1, buy, GetQuantity());
                    }

                }
                else
                {
                    var minimum = sortedLevels.Min;
                    level = new OrderbookLevel(buy ? minimum.Price - 1 : minimum.Price + 1, buy, GetQuantity());
                }

                levels.Add(level);
                sortedLevels.Add(level);

                return new OrderbookLevelUpdate(OrderbookUpdateType.Add, level);
            }


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
