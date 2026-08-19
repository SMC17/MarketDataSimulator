using System;
using System.Collections.Generic;
using System.Linq;
using MarketData.Common.Books;
using MarketData.Common.Matching;
using MarketData.Tests.Framework;
using Xunit;

namespace MarketData.Tests
{
    public class MatchingTests
    {
        private const int MinPrice = -50;
        private const int MaxPrice = 50;

        private static LimitOrderBook NewBook() => new LimitOrderBook(MinPrice, MaxPrice);
        private static List<MarketEvent> Events() => new List<MarketEvent>();

        private static ulong Rest(LimitOrderBook book, ulong id, Side side, int price, uint quantity)
        {
            book.Submit(id, side, OrderType.Limit, TimeInForce.GoodTilCancel, price, quantity, Events());
            return id;
        }

        // ---------------------------------------------------------------- priority

        [Fact]
        public void FillsTakeBestPriceFirst()
        {
            var book = NewBook();
            Rest(book, 1, Side.Ask, 12, 100);
            Rest(book, 2, Side.Ask, 10, 100);
            Rest(book, 3, Side.Ask, 11, 100);

            var events = Events();
            book.Submit(99, Side.Bid, OrderType.Limit, TimeInForce.GoodTilCancel, 12, 250, events);

            var trades = events.Where(e => e.Type == MarketEventType.Traded).ToList();
            Assert.Equal(new[] { 10, 11, 12 }, trades.Select(t => t.Price));
            Assert.Equal(new[] { 100u, 100u, 50u }, trades.Select(t => t.Quantity));
        }

        [Fact]
        public void AtOnePriceFillsTakeOldestFirst()
        {
            var book = NewBook();
            Rest(book, 1, Side.Ask, 10, 50);
            Rest(book, 2, Side.Ask, 10, 50);
            Rest(book, 3, Side.Ask, 10, 50);

            var events = Events();
            book.Submit(99, Side.Bid, OrderType.Limit, TimeInForce.GoodTilCancel, 10, 120, events);

            var trades = events.Where(e => e.Type == MarketEventType.Traded).ToList();
            Assert.Equal(new ulong[] { 1, 2, 3 }, trades.Select(t => t.OrderId));
            Assert.Equal(new[] { 50u, 50u, 20u }, trades.Select(t => t.Quantity));
        }

        [Fact]
        public void CancellingDoesNotDisturbTheRestOfTheQueue()
        {
            var book = NewBook();
            Rest(book, 1, Side.Ask, 10, 50);
            Rest(book, 2, Side.Ask, 10, 50);
            Rest(book, 3, Side.Ask, 10, 50);

            Assert.True(book.Cancel(2, Events()));

            var events = Events();
            book.Submit(99, Side.Bid, OrderType.Limit, TimeInForce.GoodTilCancel, 10, 100, events);

            // 1 was ahead of 2 and stays ahead; 3 was behind and stays behind.
            Assert.Equal(new ulong[] { 1, 3 },
                events.Where(e => e.Type == MarketEventType.Traded).Select(t => t.OrderId));
        }

        [Fact]
        public void ReducingKeepsQueuePositionAndGrowingIsRefused()
        {
            var book = NewBook();
            Rest(book, 1, Side.Ask, 10, 100);
            Rest(book, 2, Side.Ask, 10, 100);

            Assert.True(book.Reduce(1, 40, Events()));
            Assert.False(book.Reduce(1, 500, Events()));  // growing in place would steal priority

            var events = Events();
            book.Submit(99, Side.Bid, OrderType.Limit, TimeInForce.GoodTilCancel, 10, 60, events);

            var trades = events.Where(e => e.Type == MarketEventType.Traded).ToList();
            Assert.Equal(new ulong[] { 1, 2 }, trades.Select(t => t.OrderId));
            Assert.Equal(new[] { 40u, 20u }, trades.Select(t => t.Quantity));
        }

        // ---------------------------------------------------------------- order types

        [Fact]
        public void RestingLimitOrderSitsAtItsPrice()
        {
            var book = NewBook();
            var result = book.Submit(1, Side.Bid, OrderType.Limit, TimeInForce.GoodTilCancel, -5, 100, Events());

            Assert.False(result.Rejected);
            Assert.Equal(0u, result.FilledQuantity);
            Assert.Equal(100u, result.RestingQuantity);
            Assert.True(book.TryGetBest(Side.Bid, out var price, out var quantity));
            Assert.Equal(-5, price);
            Assert.Equal(100UL, quantity);
        }

        [Fact]
        public void MarketOrderNeverRests()
        {
            var book = NewBook();
            Rest(book, 1, Side.Ask, 10, 40);

            var result = book.Submit(2, Side.Bid, OrderType.Market, TimeInForce.GoodTilCancel, 0, 100, Events());

            Assert.Equal(40u, result.FilledQuantity);
            Assert.Equal(0u, result.RestingQuantity);
            Assert.Equal(0, book.OrderCount);
        }

        [Fact]
        public void MarketToLimitTradesOnlyAtTheTouchAndRestsThere()
        {
            var book = NewBook();
            Rest(book, 1, Side.Ask, 10, 40);
            Rest(book, 2, Side.Ask, 11, 100);

            var events = Events();
            var result = book.Submit(3, Side.Bid, OrderType.MarketToLimit,
                TimeInForce.GoodTilCancel, 0, 100, events);

            Assert.False(result.Rejected);
            Assert.Equal(40u, result.FilledQuantity);
            Assert.Equal(60u, result.RestingQuantity);
            Assert.Equal(new[] { 10 }, events.Where(e => e.Type == MarketEventType.Traded)
                .Select(e => e.Price));
            Assert.Equal(10, book.Find(3)?.Price);
            Assert.True(book.TryGetBest(Side.Ask, out var ask, out var askQuantity));
            Assert.Equal(11, ask);
            Assert.Equal(100UL, askQuantity);
        }

        [Fact]
        public void MarketToLimitRejectsAnEmptyOppositeBook()
        {
            var book = NewBook();
            var events = Events();

            var result = book.Submit(1, Side.Bid, OrderType.MarketToLimit,
                TimeInForce.GoodTilCancel, 0, 100, events);

            Assert.True(result.Rejected);
            Assert.Equal(0, book.OrderCount);
            Assert.Single(events, e => e.Type == MarketEventType.Rejected);
        }

        [Fact]
        public void GoodTilCrossingNeverTakesLiquidity()
        {
            var book = NewBook();
            Rest(book, 1, Side.Ask, 10, 40);

            var crossingEvents = Events();
            var crossing = book.Submit(2, Side.Bid, OrderType.Limit,
                TimeInForce.GoodTilCrossing, 10, 100, crossingEvents);

            Assert.True(crossing.Rejected);
            Assert.DoesNotContain(crossingEvents, e => e.Type == MarketEventType.Traded);
            Assert.Equal(40u, book.Find(1)?.Remaining);

            var passive = book.Submit(3, Side.Bid, OrderType.Limit,
                TimeInForce.GoodTilCrossing, 9, 100, Events());

            Assert.False(passive.Rejected);
            Assert.Equal(100u, passive.RestingQuantity);
            Assert.Equal(9, book.Find(3)?.Price);
        }

        [Fact]
        public void InvalidOrderInstructionsRejectWithoutMutatingTheBook()
        {
            var book = NewBook();
            Rest(book, 1, Side.Ask, 10, 40);

            Assert.True(book.Submit(0, Side.Bid, OrderType.Limit,
                TimeInForce.GoodTilCancel, 9, 10, Events()).Rejected);
            Assert.True(book.Submit(2, Side.Bid, OrderType.MarketToLimit,
                TimeInForce.ImmediateOrCancel, 0, 10, Events()).Rejected);
            Assert.True(book.Submit(3, Side.Bid, OrderType.Market,
                TimeInForce.GoodTilCrossing, 0, 10, Events()).Rejected);
            Assert.True(book.Submit(4, (Side)byte.MaxValue, OrderType.Limit,
                TimeInForce.GoodTilCancel, 9, 10, Events()).Rejected);
            Assert.True(book.Submit(5, Side.Bid, (OrderType)byte.MaxValue,
                TimeInForce.GoodTilCancel, 9, 10, Events()).Rejected);
            Assert.True(book.Submit(6, Side.Bid, OrderType.Limit,
                (TimeInForce)byte.MaxValue, 9, 10, Events()).Rejected);

            Assert.Equal(1, book.OrderCount);
            Assert.Equal(40u, book.Find(1)?.Remaining);
        }

        [Fact]
        public void ImmediateOrCancelTakesWhatItCanAndLeavesNothing()
        {
            var book = NewBook();
            Rest(book, 1, Side.Ask, 10, 40);

            var result = book.Submit(2, Side.Bid, OrderType.Limit, TimeInForce.ImmediateOrCancel, 10, 100, Events());

            Assert.Equal(40u, result.FilledQuantity);
            Assert.Equal(0u, result.RestingQuantity);
            Assert.Equal(0, book.OrderCount);
        }

        [Fact]
        public void FillOrKillIsAllOrNothingAndLeavesTheBookUntouched()
        {
            var book = NewBook();
            Rest(book, 1, Side.Ask, 10, 40);

            var events = Events();
            var result = book.Submit(2, Side.Bid, OrderType.Limit, TimeInForce.FillOrKill, 10, 100, events);

            Assert.True(result.Rejected);
            Assert.Equal(0u, result.FilledQuantity);
            Assert.DoesNotContain(events, e => e.Type == MarketEventType.Traded);

            // The resting order must be entirely untouched - no partial fill was applied and rolled back.
            Assert.True(book.TryGetBest(Side.Ask, out _, out var quantity));
            Assert.Equal(40UL, quantity);

            // And a fillable one goes through.
            Assert.False(book.Submit(3, Side.Bid, OrderType.Limit, TimeInForce.FillOrKill, 10, 40, Events()).Rejected);
            Assert.Equal(0, book.OrderCount);
        }

        [Fact]
        public void OrdersOutsideTheBandAndDuplicateIdsAreRejected()
        {
            var book = NewBook();
            Rest(book, 1, Side.Bid, -5, 100);

            Assert.True(book.Submit(2, Side.Bid, OrderType.Limit, TimeInForce.GoodTilCancel, MaxPrice + 1, 10, Events()).Rejected);
            Assert.True(book.Submit(1, Side.Bid, OrderType.Limit, TimeInForce.GoodTilCancel, -5, 10, Events()).Rejected);
            Assert.True(book.Submit(3, Side.Bid, OrderType.Limit, TimeInForce.GoodTilCancel, -5, 0, Events()).Rejected);
            Assert.Equal(1, book.OrderCount);
        }

        // ---------------------------------------------------------------- invariants

        /// <summary>
        /// Nothing is created or destroyed: every unit submitted is either filled, still resting,
        /// or explicitly cancelled. A matching engine that fails this is inventing or losing
        /// tradeable quantity, which is the worst thing it could do.
        /// </summary>
        [Fact]
        public void QuantityIsConservedAcrossArbitraryOrderFlow()
        {
            Property.ForAll(
                generate: random => random.Next(),
                shrink: _ => Array.Empty<int>(),
                describe: seed => $"seed {seed}",
                cases: 60,
                property: seed =>
                {
                    var random = new Random(seed);
                    var book = NewBook();
                    var events = Events();

                    ulong submitted = 0, cancelled = 0;
                    var live = new List<ulong>();
                    ulong nextId = 1;

                    for (var i = 0; i < 1500; i++)
                    {
                        if (live.Count > 0 && random.NextDouble() < 0.4)
                        {
                            var victim = live[random.Next(live.Count)];
                            var before = events.Count;

                            if (book.Cancel(victim, events))
                            {
                                for (var e = before; e < events.Count; e++)
                                    if (events[e].Type == MarketEventType.Cancelled)
                                        cancelled += events[e].Quantity;
                            }

                            live.Remove(victim);
                            continue;
                        }

                        var side = random.Next(2) == 0 ? Side.Bid : Side.Ask;
                        var quantity = (uint)random.Next(1, 500);
                        var id = nextId++;
                        var result = book.Submit(id, side, OrderType.Limit, TimeInForce.GoodTilCancel,
                            random.Next(MinPrice, MaxPrice + 1), quantity, events);

                        if (!result.Rejected)
                        {
                            submitted += quantity;

                            if (result.RestingQuantity > 0)
                                live.Add(id);
                        }
                    }

                    ulong traded = 0;

                    foreach (var e in events.Where(e => e.Type == MarketEventType.Traded))
                        traded += e.Quantity;

                    ulong resting = 0;

                    foreach (var side in new[] { Side.Bid, Side.Ask })
                    {
                        var depth = new PriceLevel[MaxPrice - MinPrice + 1];
                        var count = book.CopyDepth(side, depth);

                        for (var i = 0; i < count; i++)
                            resting += depth[i].Quantity;
                    }

                    // Each trade consumes one unit from an aggressor and one from a resting order,
                    // so traded quantity is counted twice against what was submitted.
                    Assert.Equal(submitted, traded * 2 + resting + cancelled);
                });
        }

        [Fact]
        public void BookNeverCrossesAfterMatching()
        {
            Property.ForAll(
                generate: random => random.Next(),
                shrink: _ => Array.Empty<int>(),
                describe: seed => $"seed {seed}",
                cases: 60,
                property: seed =>
                {
                    var random = new Random(seed);
                    var book = NewBook();
                    ulong nextId = 1;

                    for (var i = 0; i < 2000; i++)
                    {
                        book.Submit(nextId++, random.Next(2) == 0 ? Side.Bid : Side.Ask,
                            OrderType.Limit, TimeInForce.GoodTilCancel,
                            random.Next(MinPrice, MaxPrice + 1), (uint)random.Next(1, 200), null);

                        if (book.TryGetBest(Side.Bid, out var bid, out _) &&
                            book.TryGetBest(Side.Ask, out var ask, out _))
                        {
                            Assert.True(bid < ask,
                                $"book crossed after order {i}: best bid {bid} >= best ask {ask}");
                        }
                    }
                });
        }

        // ---------------------------------------------------------------- differential

        /// <summary>
        /// The optimised book against a naive one that spells price-time priority out literally.
        /// Compared after every operation, so a divergence names the operation that caused it.
        /// </summary>
        [Fact]
        public void MatchesTheReferenceImplementationUnderArbitraryFlow()
        {
            Property.ForAll(
                generate: random => random.Next(),
                shrink: _ => Array.Empty<int>(),
                describe: seed => $"seed {seed}",
                cases: 40,
                property: seed =>
                {
                    var random = new Random(seed);
                    var book = NewBook();
                    var reference = new ReferenceBook();

                    var live = new List<ulong>();
                    ulong nextId = 1;

                    for (var step = 0; step < 800; step++)
                    {
                        var actualEvents = Events();
                        var referenceEvents = Events();
                        string action;

                        var roll = random.NextDouble();

                        if (live.Count > 0 && roll < 0.30)
                        {
                            var victim = live[random.Next(live.Count)];
                            action = $"Cancel({victim})";

                            Assert.Equal(reference.Cancel(victim, referenceEvents), book.Cancel(victim, actualEvents));
                            live.Remove(victim);
                        }
                        else if (live.Count > 0 && roll < 0.40)
                        {
                            var victim = live[random.Next(live.Count)];
                            var quantity = (uint)random.Next(0, 200);
                            action = $"Reduce({victim}, {quantity})";

                            Assert.Equal(reference.Reduce(victim, quantity, referenceEvents),
                                book.Reduce(victim, quantity, actualEvents));

                            if (quantity == 0)
                                live.Remove(victim);
                        }
                        else
                        {
                            var side = random.Next(2) == 0 ? Side.Bid : Side.Ask;
                            var typeRoll = random.NextDouble();
                            var type = typeRoll switch
                            {
                                < 0.08 => OrderType.Market,
                                < 0.14 => OrderType.MarketToLimit,
                                _ => OrderType.Limit,
                            };
                            var tif = type == OrderType.MarketToLimit
                                ? TimeInForce.GoodTilCancel
                                : random.NextDouble() switch
                                {
                                    < 0.10 => TimeInForce.ImmediateOrCancel,
                                    < 0.16 => TimeInForce.FillOrKill,
                                    < 0.24 when type == OrderType.Limit => TimeInForce.GoodTilCrossing,
                                    _ => TimeInForce.GoodTilCancel,
                                };
                            var price = random.Next(MinPrice, MaxPrice + 1);
                            var quantity = (uint)random.Next(1, 300);
                            var id = nextId++;
                            action = $"Submit(#{id}, {side}, {type}, {tif}, {price}, {quantity})";

                            var actual = book.Submit(id, side, type, tif, price, quantity, actualEvents);
                            var expected = reference.Submit(id, side, type, tif, price, quantity,
                                MinPrice, MaxPrice, referenceEvents);

                            Assert.True(expected == actual, $"result mismatch after {action}: {expected} vs {actual}");

                            if (!actual.Rejected && actual.RestingQuantity > 0)
                                live.Add(id);
                        }

                        // Trades must agree exactly - same resting orders, same prices, same sizes,
                        // in the same order. This is what pins down price-time priority.
                        var actualTrades = actualEvents.Where(e => e.Type == MarketEventType.Traded).ToList();
                        var referenceTrades = referenceEvents.Where(e => e.Type == MarketEventType.Traded).ToList();

                        Assert.True(referenceTrades.SequenceEqual(actualTrades),
                            $"trades diverged after {action}\n" +
                            $"  reference: [{string.Join(", ", referenceTrades)}]\n" +
                            $"  actual:    [{string.Join(", ", actualTrades)}]");

                        foreach (var side in new[] { Side.Bid, Side.Ask })
                        {
                            var expectedQueue = reference.Queue(side);
                            var actualQueue = Queue(book, side);

                            Assert.True(expectedQueue.SequenceEqual(actualQueue),
                                $"{side} queue diverged after {action}\n" +
                                $"  reference: [{string.Join(", ", expectedQueue)}]\n" +
                                $"  actual:    [{string.Join(", ", actualQueue)}]");
                        }
                    }
                });
        }

        /// <summary>Walks the real book in price-time order, for comparison with the reference.</summary>
        private static List<(ulong Id, Side Side, int Price, uint Remaining)> Queue(LimitOrderBook book, Side side)
            => book.OrdersInPriority(side).Select(o => (o.Id, o.Side, o.Price, o.Remaining)).ToList();
    }
}
