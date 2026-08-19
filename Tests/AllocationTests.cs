using System;
using System.Collections.Generic;
using MarketData.Common.Books;
using MarketData.Common.Matching;
using Xunit;

namespace MarketData.Tests
{
    /// <summary>
    /// Allocation budgets for the hot paths, enforced as tests.
    /// </summary>
    /// <remarks>
    /// <para>
    /// In a latency-sensitive system allocation is not a throughput concern to be tuned later - it
    /// is a source of pauses that land at arbitrary moments, including the worst ones. The only
    /// way to keep a path allocation-free is to assert it, because a single incautious edit
    /// reintroduces it silently and nothing else will notice.
    /// </para>
    /// <para>
    /// These measure steady state specifically: pools and hash tables are warmed first, so what is
    /// asserted is that running the system does not allocate, not that starting it does not.
    /// </para>
    /// </remarks>
    public class AllocationTests
    {
        private const int PriceBand = 1024;

        private static long BytesPerIteration(Action<int> action, int warmupIterations, int iterations)
        {
            for (var i = 0; i < warmupIterations; i++)
                action(i);

            var before = GC.GetAllocatedBytesForCurrentThread();

            for (var i = 0; i < iterations; i++)
                action(i);

            return (GC.GetAllocatedBytesForCurrentThread() - before) / iterations;
        }

        [Fact]
        public void SteadyStateAddAndCancelAllocatesNothing()
        {
            var book = new LimitOrderBook(-PriceBand, PriceBand);
            ulong nextId = 1;

            for (var i = 0; i < 5_000; i++)
            {
                book.Submit(nextId++, (i & 1) == 0 ? Side.Bid : Side.Ask, OrderType.Limit,
                    TimeInForce.GoodTilCancel, (i & 1) == 0 ? -1 - (i % 32) : 1 + (i % 32), 100, null);
            }

            void Cycle(int i)
            {
                var id = nextId++;
                var side = (i & 1) == 0 ? Side.Bid : Side.Ask;
                var price = side == Side.Bid ? -1 - (i % 32) : 1 + (i % 32);

                book.Submit(id, side, OrderType.Limit, TimeInForce.GoodTilCancel, price, 100, null);
                book.Cancel(id, null);
            }

            var bytes = BytesPerIteration(Cycle, warmupIterations: 20_000, iterations: 200_000);

            Assert.True(bytes == 0, $"expected zero allocation per add/cancel cycle, measured {bytes} bytes");
        }

        [Fact]
        public void SteadyStateMatchingAllocatesNothing()
        {
            var book = new LimitOrderBook(-PriceBand, PriceBand);
            var events = new List<MarketEvent>(1024);
            ulong nextId = 1;

            void Cycle(int i)
            {
                events.Clear();

                var side = (i & 1) == 0 ? Side.Bid : Side.Ask;
                var opposite = side == Side.Bid ? Side.Ask : Side.Bid;
                var price = opposite == Side.Bid ? -1 - (i % 16) : 1 + (i % 16);

                book.Submit(nextId++, opposite, OrderType.Limit, TimeInForce.GoodTilCancel, price, 100, events);
                book.Submit(nextId++, side, OrderType.Limit, TimeInForce.ImmediateOrCancel, price, 100, events);
            }

            var bytes = BytesPerIteration(Cycle, warmupIterations: 20_000, iterations: 200_000);

            Assert.True(bytes == 0, $"expected zero allocation per match cycle, measured {bytes} bytes");
        }

        [Fact]
        public void PublishingDepthAllocatesNothing()
        {
            var book = new LimitOrderBook(-PriceBand, PriceBand);
            var buffer = new PriceLevel[10];
            ulong nextId = 1;

            for (var i = 0; i < 2_000; i++)
            {
                book.Submit(nextId++, (i & 1) == 0 ? Side.Bid : Side.Ask, OrderType.Limit,
                    TimeInForce.GoodTilCancel, (i & 1) == 0 ? -1 - (i % 64) : 1 + (i % 64), 100, null);
            }

            void Publish(int i) => book.CopyDepth((i & 1) == 0 ? Side.Bid : Side.Ask, buffer);

            var bytes = BytesPerIteration(Publish, warmupIterations: 10_000, iterations: 200_000);

            Assert.True(bytes == 0, $"expected zero allocation per depth publish, measured {bytes} bytes");
        }

        /// <summary>
        /// The aggregated books sit on the dissemination path and must be allocation-free too. The
        /// tree is excluded deliberately: enumerating a SortedSet allocates its traversal stack,
        /// which is exactly the finding recorded in BENCHMARKS.md.
        /// </summary>
        [Theory]
        [InlineData("SortedArray")]
        [InlineData("Vectorized")]
        [InlineData("Ladder")]
        public void AggregatedBookPublishAllocatesNothing(string implementation)
        {
            var book = BookFactory.Create(implementation, 10, PriceBand);
            var buffer = new PriceLevel[10];

            for (var i = 0; i < 10; i++)
            {
                book.Upsert(Side.Bid, -1 - i, 100);
                book.Upsert(Side.Ask, 1 + i, 100);
            }

            void Publish(int i)
            {
                book.Upsert(Side.Bid, -1 - (i % 10), (uint)(100 + (i % 7)));
                book.CopyTo(Side.Bid, buffer);
                book.CopyTo(Side.Ask, buffer);
            }

            var bytes = BytesPerIteration(Publish, warmupIterations: 10_000, iterations: 200_000);

            Assert.True(bytes == 0, $"expected zero allocation per publish, measured {bytes} bytes");
        }

        /// <summary>
        /// The wire encoder must not allocate either: it runs once per update on the publish path.
        /// </summary>
        [Fact]
        public void FeedEncodingAllocatesNothing()
        {
            var buffer = new byte[MarketData.Common.Feed.FeedProtocol.MaxPacketSize];

            void Encode(int i)
            {
                var offset = MarketData.Common.Feed.FeedProtocol.HeaderSize;
                offset += MarketData.Common.Feed.FeedProtocol.WriteIncremental(buffer.AsSpan(offset),
                    MarketData.Common.Feed.FeedMessageType.Add, 1, Side.Bid, new PriceLevel(-i % 100, 100));
                MarketData.Common.Feed.FeedProtocol.WriteHeader(buffer.AsSpan(0, offset), 1, 7, (ulong)i, i);
            }

            var bytes = BytesPerIteration(Encode, warmupIterations: 10_000, iterations: 200_000);

            Assert.True(bytes == 0, $"expected zero allocation per encode, measured {bytes} bytes");
        }
    }
}
