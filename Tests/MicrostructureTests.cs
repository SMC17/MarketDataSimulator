using System;
using MarketData.Common.Analytics;
using MarketData.Tests.Framework;
using Xunit;

namespace MarketData.Tests
{
    public class MicrostructureTests
    {
        private static Quote Q(int bidPrice, uint bidSize, int askPrice, uint askSize)
            => new Quote(bidPrice, bidSize, askPrice, askSize);

        // ---------------------------------------------------------------- order flow imbalance

        [Fact]
        public void UnchangedQuotesContributeNothing()
        {
            var quote = Q(100, 50, 101, 60);
            Assert.Equal(0, MicrostructureMonitor.Contribution(quote, quote));
        }

        [Fact]
        public void SizeAddedAtTheBidIsBuyingPressure()
        {
            // Bid price unchanged, queue grows by 30.
            Assert.Equal(30, MicrostructureMonitor.Contribution(Q(100, 50, 101, 60), Q(100, 80, 101, 60)));
        }

        [Fact]
        public void SizeRemovedFromTheAskIsAlsoBuyingPressure()
        {
            // Whether it was cancelled or lifted is invisible in the book, and irrelevant here.
            Assert.Equal(25, MicrostructureMonitor.Contribution(Q(100, 50, 101, 60), Q(100, 50, 101, 35)));
        }

        [Fact]
        public void ABidSteppingUpCountsItsWholeNewQueue()
        {
            // The old queue is gone from the touch and the new one is entirely fresh interest.
            var contribution = MicrostructureMonitor.Contribution(Q(100, 50, 105, 60), Q(101, 40, 105, 60));
            Assert.Equal(40, contribution);
        }

        [Fact]
        public void ABidSteppingDownCountsTheWholeQueueThatLeft()
        {
            var contribution = MicrostructureMonitor.Contribution(Q(100, 50, 105, 60), Q(99, 40, 105, 60));
            Assert.Equal(-50, contribution);
        }

        [Fact]
        public void BuyingAndSellingPressureAreAntisymmetric()
        {
            Property.ForAll(
                generate: random =>
                {
                    var bid = random.Next(-50, 50);
                    return (Before: Q(bid, (uint)random.Next(1, 500), bid + random.Next(1, 20), (uint)random.Next(1, 500)),
                            AfterBid: bid + random.Next(-3, 4));
                },
                shrink: _ => Array.Empty<(Quote, int)>(),
                describe: c => $"{c.Before} -> bid {c.AfterBid}",
                property: c =>
                {
                    var after = Q(c.AfterBid, c.Before.BidSize, c.Before.AskPrice, c.Before.AskSize);

                    // Mirroring the book across the touch must flip the sign of the pressure.
                    var forward = MicrostructureMonitor.Contribution(c.Before, after);
                    var mirroredBefore = Q(-c.Before.AskPrice, c.Before.AskSize, -c.Before.BidPrice, c.Before.BidSize);
                    var mirroredAfter = Q(-after.AskPrice, after.AskSize, -after.BidPrice, after.BidSize);
                    var mirrored = MicrostructureMonitor.Contribution(mirroredBefore, mirroredAfter);

                    Assert.Equal(-forward, mirrored);
                });
        }

        [Fact]
        public void FlowAccumulatesAndResets()
        {
            var monitor = new MicrostructureMonitor();

            monitor.Update(Q(100, 50, 101, 60));   // first quote establishes a baseline only
            Assert.Equal(0, monitor.OrderFlowImbalance);

            monitor.Update(Q(100, 70, 101, 60));   // +20 at the bid
            monitor.Update(Q(100, 70, 101, 45));   // +15 off the ask
            Assert.Equal(35, monitor.OrderFlowImbalance);

            Assert.Equal(35, monitor.ResetFlow());
            Assert.Equal(0, monitor.OrderFlowImbalance);

            // Resetting must not break continuity of the next delta.
            monitor.Update(Q(100, 80, 101, 45));
            Assert.Equal(10, monitor.OrderFlowImbalance);
        }

        // ---------------------------------------------------------------- quote arithmetic

        [Fact]
        public void MicropriceLiesBetweenTheQuotesAndLeansTowardTheThinSide()
        {
            var balanced = Q(100, 50, 102, 50);
            Assert.Equal(101, balanced.Microprice, 6);

            // A heavy bid against a thin ask should sit nearer the ask.
            var heavyBid = Q(100, 900, 102, 100);
            Assert.True(heavyBid.Microprice > 101, $"expected a lean toward the ask, got {heavyBid.Microprice}");
            Assert.InRange(heavyBid.Microprice, 100, 102);
        }

        [Fact]
        public void ImbalanceIsBoundedAndSigned()
        {
            Assert.Equal(0, Q(100, 50, 101, 50).Imbalance, 9);
            Assert.Equal(1, Q(100, 50, 101, 0).Imbalance, 9);
            Assert.Equal(-1, Q(100, 0, 101, 50).Imbalance, 9);
        }

        [Fact]
        public void MidIsExactInHalfTicks()
        {
            // An odd spread has no exact integer mid, which is why it is carried doubled.
            Assert.Equal(201, Q(100, 1, 101, 1).MidHalfTicks);
            Assert.Equal(1, Q(100, 1, 101, 1).Spread);
        }

        // ---------------------------------------------------------------- regression

        [Fact]
        public void RegressionRecoversAKnownLine()
        {
            var regression = new OnlineRegression();

            for (var x = 0; x < 1000; x++)
                regression.Add(x, 3.5 * x + 17);

            Assert.Equal(3.5, regression.Slope, 9);
            Assert.Equal(17, regression.Intercept, 6);
            Assert.Equal(1.0, regression.RSquared, 9);
        }

        [Fact]
        public void RegressionFindsNoRelationshipWhereThereIsNone()
        {
            var regression = new OnlineRegression();
            var random = new Random(11);

            for (var i = 0; i < 20_000; i++)
                regression.Add(random.NextDouble(), random.NextDouble());

            Assert.True(Math.Abs(regression.Correlation) < 0.05,
                $"expected no correlation between independent draws, got {regression.Correlation}");
            Assert.True(Math.Abs(regression.SlopeTStatistic) < 4,
                $"expected an insignificant slope, got t = {regression.SlopeTStatistic}");
        }

        /// <summary>
        /// The reason for the Welford form: raw sums of squares lose their significant digits when
        /// the mean is far from zero, which is exactly what a session-long flow accumulator looks
        /// like.
        /// </summary>
        [Fact]
        public void RegressionStaysAccurateWithAHugeOffset()
        {
            var regression = new OnlineRegression();
            const double offset = 1e9;

            for (var x = 0; x < 5000; x++)
                regression.Add(offset + x, 2.0 * (offset + x) + 5);

            Assert.Equal(2.0, regression.Slope, 6);
            Assert.Equal(1.0, regression.RSquared, 6);
        }

        [Fact]
        public void MonitoringAndRegressionAllocateNothing()
        {
            var monitor = new MicrostructureMonitor();
            var regression = new OnlineRegression();

            void Cycle(int i)
            {
                monitor.Update(Q(100 + (i % 5), (uint)(50 + (i % 17)), 106 + (i % 5), (uint)(40 + (i % 13))));
                regression.Add(monitor.OrderFlowImbalance, monitor.Current.MidHalfTicks);
            }

            for (var i = 0; i < 20_000; i++)
                Cycle(i);

            var before = GC.GetAllocatedBytesForCurrentThread();

            for (var i = 0; i < 200_000; i++)
                Cycle(i);

            var bytes = (GC.GetAllocatedBytesForCurrentThread() - before) / 200_000;

            Assert.True(bytes == 0, $"expected zero allocation per update, measured {bytes} bytes");
        }
    }
}
