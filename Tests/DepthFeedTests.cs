using System;
using System.Collections.Generic;
using System.Linq;
using MarketData.Common.Books;
using MarketData.Common.Matching;
using MarketData.Tests.Framework;
using Xunit;

namespace MarketData.Tests
{
    /// <summary>
    /// The depth feed against the engine that produced it.
    /// </summary>
    public class DepthFeedTests
    {
        private const int PriceBand = 512;
        private const int Depth = 10;

        /// <summary>
        /// The property the whole architecture rests on: a subscriber that applies only the
        /// derived depth updates ends up with exactly the aggregated view of the engine's
        /// order-by-order book.
        /// </summary>
        /// <remarks>
        /// Checked after every batch, so a divergence names the batch that caused it rather than
        /// surfacing thousands of updates later. If this fails, every consumer downstream is
        /// trading on depth the exchange never had.
        /// </remarks>
        [Fact]
        public void DerivedDepthFeedReconstructsTheEngineBook()
        {
            Property.ForAll(
                generate: random => random.Next(),
                shrink: _ => Array.Empty<int>(),
                describe: seed => $"seed {seed}",
                cases: 40,
                property: seed =>
                {
                    var random = new Random(seed);
                    var engine = new LimitOrderBook(-PriceBand, PriceBand);
                    var flow = new OrderFlowSimulator(engine);
                    var projection = new DepthProjection();

                    // What a subscriber builds from the depth feed alone. Deliberately unbounded
                    // in depth, so it mirrors the whole book rather than a truncated window.
                    var subscriber = new TreeBook(PriceBand * 2);

                    var events = new List<MarketEvent>(64);
                    var changes = new List<LevelChange>(64);

                    for (var step = 0; step < 3000; step++)
                    {
                        events.Clear();
                        changes.Clear();

                        flow.Step(random, events);

                        if (events.Count == 0)
                            continue;

                        projection.Project(engine, events, changes);

                        foreach (var change in changes)
                        {
                            if (change.IsRemoval)
                                subscriber.Remove(change.Side, change.Price);
                            else
                                subscriber.Upsert(change.Side, change.Price, (uint)change.Quantity);
                        }

                        foreach (var side in new[] { Side.Bid, Side.Ask })
                        {
                            var expected = new PriceLevel[PriceBand * 2];
                            var count = engine.CopyDepth(side, expected);
                            var actual = subscriber.ToList(side);

                            Assert.True(expected.Take(count).SequenceEqual(actual),
                                $"depth diverged on {side} at step {step}\n" +
                                $"  engine:     [{string.Join(", ", expected.Take(count))}]\n" +
                                $"  subscriber: [{string.Join(", ", actual)}]");
                        }
                    }
                });
        }

        /// <summary>
        /// Order flow must never produce a crossed book, whatever the generator does.
        /// </summary>
        [Fact]
        public void MatchingDrivenFlowNeverCrossesTheBook()
        {
            Property.ForAll(
                generate: random => random.Next(),
                shrink: _ => Array.Empty<int>(),
                describe: seed => $"seed {seed}",
                cases: 40,
                property: seed =>
                {
                    var random = new Random(seed);
                    var engine = new LimitOrderBook(-PriceBand, PriceBand);
                    var flow = new OrderFlowSimulator(engine);
                    var events = new List<MarketEvent>(64);

                    for (var step = 0; step < 5000; step++)
                    {
                        events.Clear();
                        flow.Step(random, events);

                        if (engine.TryGetBest(Side.Bid, out var bid, out _) &&
                            engine.TryGetBest(Side.Ask, out var ask, out _))
                        {
                            Assert.True(bid < ask, $"book crossed at step {step}: bid {bid} >= ask {ask}");
                        }
                    }
                });
        }

        /// <summary>
        /// The generator must keep a live book rather than emptying it or letting it run away, or
        /// the feed it drives is not exercising anything.
        /// </summary>
        [Fact]
        public void FlowSustainsATwoSidedBook()
        {
            var random = new Random(4242);
            var engine = new LimitOrderBook(-PriceBand, PriceBand);
            var flow = new OrderFlowSimulator(engine);
            var events = new List<MarketEvent>(64);
            var trades = 0;

            for (var step = 0; step < 20_000; step++)
            {
                events.Clear();
                flow.Step(random, events);
                trades += events.Count(e => e.Type == MarketEventType.Traded);

                if (step % 4096 == 0)
                    flow.Compact();
            }

            Assert.True(engine.TryGetBest(Side.Bid, out _, out _), "no bids after 20,000 steps");
            Assert.True(engine.TryGetBest(Side.Ask, out _, out _), "no asks after 20,000 steps");
            Assert.True(trades > 100, $"expected the flow to generate trades, saw {trades}");
            Assert.InRange(engine.OrderCount, 10, 200_000);
        }
    }
}
