using System;
using System.Collections.Generic;
using System.Linq;
using MarketData.Common.Books;
using MarketData.Common.Matching;
using MarketData.Common.Risk;
using Xunit;

namespace MarketData.Tests
{
    /// <summary>
    /// The risk layer sits on the live order path, and must not change what that path produces
    /// while it is admitting everything.
    /// </summary>
    /// <remarks>
    /// This is the test that lets the gate be deployed at all. Every transport measurement in
    /// BENCHMARKS.md was taken against a particular generated stream; if inserting the gate
    /// perturbs that stream, the numbers describe a system that no longer exists. So the identity
    /// is asserted event-for-event rather than argued from the code.
    /// </remarks>
    public class RiskOnThePathTests
    {
        private const int Band = 512;
        private const int InstrumentId = 1;
        private const ulong BidAccount = 77;
        private const ulong AskAccount = 78;

        private static List<string> Render(IEnumerable<MarketEvent> events)
            => events.Select(e => e.ToString()).ToList();

        private static List<string> RunUnmanaged(int seed, int steps)
        {
            var flow = new OrderFlowSimulator(new LimitOrderBook(-Band, Band));
            var random = new Random(seed);
            var seen = new List<MarketEvent>();

            for (var i = 0; i < steps; i++)
                flow.Step(random, seen);

            return Render(seen);
        }

        private static (List<string> Events, long Rejections) RunManaged(int seed, int steps,
            RiskLimits limits)
        {
            var risk = new PreTradeRiskEngine();
            risk.ConfigureAccount(BidAccount, limits);
            risk.ConfigureAccount(AskAccount, limits);

            var managed = new RiskManagedOrderBook(InstrumentId, -Band, Band, risk);
            var flow = new OrderFlowSimulator(managed, BidAccount, AskAccount);
            var random = new Random(seed);
            var seen = new List<MarketEvent>();

            for (var i = 0; i < steps; i++)
                flow.Step(random, seen);

            return (Render(seen), flow.RiskRejections);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(20260819)]
        [InlineData(int.MaxValue)]
        public void PermissiveLimitsProduceTheIdenticalStream(int seed)
        {
            var baseline = RunUnmanaged(seed, 5_000);
            var (managed, rejections) = RunManaged(seed, 5_000, RiskLimits.Unbounded);

            Assert.Equal(0, rejections);
            Assert.Equal(baseline.Count, managed.Count);

            for (var i = 0; i < baseline.Count; i++)
            {
                Assert.True(baseline[i] == managed[i],
                    $"seed {seed} diverged at event {i}:{Environment.NewLine}" +
                    $"  unmanaged: {baseline[i]}{Environment.NewLine}" +
                    $"  managed:   {managed[i]}");
            }
        }

        /// <summary>
        /// With limits that bite, the flow legitimately diverges - that is the gate working.
        /// </summary>
        /// <remarks>
        /// Asserted so the previous test cannot pass vacuously. If the gate were wired in but never
        /// consulted, the streams would match under every limit, and the identity test above would
        /// be proving nothing at all.
        /// </remarks>
        [Fact]
        public void RestrictiveLimitsDoChangeTheStream()
        {
            var baseline = RunUnmanaged(20260819, 5_000);

            var tight = RiskLimits.Unbounded with { MaxOrderQuantity = 50 };
            var (managed, rejections) = RunManaged(20260819, 5_000, tight);

            Assert.True(rejections > 0, "a 50-lot cap against orders up to 500 must reject something");
            Assert.NotEqual(baseline.Count, managed.Count);
        }

        /// <summary>Resetting must unwind exposure, not just empty the book.</summary>
        /// <remarks>
        /// Clearing the book while leaving reservations charged would silently and permanently
        /// consume the account's credit, and nothing downstream would ever release it because
        /// nothing references those orders any more.
        /// </remarks>
        [Fact]
        public void ResettingReleasesEveryReservation()
        {
            var risk = new PreTradeRiskEngine();
            risk.ConfigureAccount(BidAccount, RiskLimits.Unbounded);
            risk.ConfigureAccount(AskAccount, RiskLimits.Unbounded);

            var managed = new RiskManagedOrderBook(InstrumentId, -Band, Band, risk);
            var flow = new OrderFlowSimulator(managed, BidAccount, AskAccount);
            var random = new Random(4242);
            var events = new List<MarketEvent>();

            for (var i = 0; i < 2_000; i++)
                flow.Step(random, events);

            Assert.True(risk.ActiveOrders > 0, "the run should have left resting orders reserved");

            flow.Reset();

            Assert.Equal(0, risk.ActiveOrders);
            Assert.Equal(0, managed.OrderCount);
        }

        /// <summary>
        /// An immediate-or-cancel order that rests nothing must not leave a reservation behind.
        /// </summary>
        /// <remarks>
        /// IOC is the case where reserve-then-release is easiest to get wrong: the order is
        /// reserved on the way in, executes partially or not at all, and is then cancelled by the
        /// book rather than by anyone calling Cancel. If the unfilled remainder is not released,
        /// every IOC permanently consumes a slice of the account's credit, and nothing downstream
        /// can ever give it back because no order id references it any more.
        /// </remarks>
        [Fact]
        public void AnUnfilledImmediateOrCancelReleasesItsReservation()
        {
            var risk = new PreTradeRiskEngine();
            risk.ConfigureAccount(BidAccount, RiskLimits.Unbounded);

            var managed = new RiskManagedOrderBook(InstrumentId, -Band, Band, risk);
            var events = new List<MarketEvent>();

            // Nothing on the far side, so this can execute nothing and rests nothing.
            var result = managed.Submit(BidAccount, 1, Side.Bid, OrderType.Limit,
                TimeInForce.ImmediateOrCancel, 100, 50, events);

            Assert.False(result.Rejected);
            Assert.Equal(0u, result.Matching.RestingQuantity);
            Assert.Equal(0, managed.OrderCount);

            Assert.Equal(0, risk.ActiveOrders);
        }

        /// <summary>A partially filled IOC releases the remainder it never rested.</summary>
        [Fact]
        public void APartiallyFilledImmediateOrCancelReleasesTheRemainder()
        {
            var risk = new PreTradeRiskEngine();
            risk.ConfigureAccount(1, RiskLimits.Unbounded);
            risk.ConfigureAccount(2, RiskLimits.Unbounded);

            var managed = new RiskManagedOrderBook(InstrumentId, -Band, Band, risk);
            var events = new List<MarketEvent>();

            // Account 1 rests 10 at 100.
            Assert.False(managed.Submit(1, 1, Side.Ask, OrderType.Limit,
                TimeInForce.GoodTilCancel, 100, 10, events).Rejected);

            // Account 2 takes 10 of an intended 40; 30 is cancelled by the book.
            var taker = managed.Submit(2, 2, Side.Bid, OrderType.Limit,
                TimeInForce.ImmediateOrCancel, 100, 40, events);

            Assert.False(taker.Rejected);
            Assert.Equal(0, managed.OrderCount);
            Assert.Equal(0, risk.ActiveOrders);
        }

        [Fact]
        public void AccountZeroIsRejectedRatherThanSilentlyAccepted()
        {
            var risk = new PreTradeRiskEngine();
            var managed = new RiskManagedOrderBook(InstrumentId, -Band, Band, risk);

            Assert.Throws<ArgumentOutOfRangeException>(() => new OrderFlowSimulator(managed, 0, AskAccount));
            Assert.Throws<ArgumentOutOfRangeException>(() => new OrderFlowSimulator(managed, BidAccount, 0));
            Assert.Throws<ArgumentNullException>(() => new OrderFlowSimulator(null, BidAccount, AskAccount));

            // Both sides on one account would make every match a self-trade.
            Assert.Throws<ArgumentException>(
                () => new OrderFlowSimulator(managed, BidAccount, BidAccount));
        }
    }
}
