using System;
using System.Collections.Generic;
using System.Linq;
using MarketData.Common.Books;
using MarketData.Common.Matching;
using MarketData.Common.Risk;
using Xunit;

namespace MarketData.Tests
{
    public sealed class RiskControlTests
    {
        private const int Instrument = 7;
        private const ulong AccountOne = 101;
        private const ulong AccountTwo = 202;

        [Fact]
        public void LimitRejectionsAreAtomicAndClassified()
        {
            var risk = new PreTradeRiskEngine();
            risk.ConfigureAccount(AccountOne, new RiskLimits(
                MaxOrderQuantity: 100,
                MaxOrderNotional: 1_000,
                MaxOpenQuantity: 150,
                MaxOpenNotional: 1_500,
                MaxAbsolutePosition: 120,
                MaxActiveOrders: 2));

            Assert.Equal(RiskRejectReason.InvalidOrder,
                risk.Reserve(AccountOne, 0, Instrument, Side.Bid, 1, 1).Reason);
            Assert.Equal(RiskRejectReason.UnknownAccount,
                risk.Reserve(999, 1, Instrument, Side.Bid, 1, 1).Reason);
            Assert.Equal(RiskRejectReason.OrderQuantityTooLarge,
                risk.Reserve(AccountOne, 1, Instrument, Side.Bid, 101, 1).Reason);
            Assert.Equal(RiskRejectReason.OrderNotionalTooLarge,
                risk.Reserve(AccountOne, 1, Instrument, Side.Bid, 100, 11).Reason);

            Assert.True(risk.Reserve(AccountOne, 1, Instrument, Side.Bid, 100, 10).Accepted);
            var before = Snapshot(risk, AccountOne);

            Assert.Equal(RiskRejectReason.DuplicateOrderId,
                risk.Reserve(AccountOne, 1, Instrument, Side.Bid, 1, 1).Reason);
            Assert.Equal(RiskRejectReason.OpenQuantityExceeded,
                risk.Reserve(AccountOne, 2, Instrument, Side.Ask, 51, 1).Reason);
            Assert.Equal(RiskRejectReason.OpenNotionalExceeded,
                risk.Reserve(AccountOne, 2, Instrument, Side.Ask, 50, 11).Reason);
            Assert.Equal(before, Snapshot(risk, AccountOne));

            Assert.True(risk.Reserve(AccountOne, 2, Instrument, Side.Ask, 10, 10).Accepted);
            Assert.Equal(RiskRejectReason.ActiveOrderLimitExceeded,
                risk.Reserve(AccountOne, 3, Instrument, Side.Ask, 1, 1).Reason);
            AssertHealthy(risk);
        }

        [Fact]
        public void ArithmeticOverflowFailsClosed()
        {
            var risk = new PreTradeRiskEngine();
            risk.ConfigureAccount(AccountOne, RiskLimits.Unbounded);

            var decision = risk.Reserve(AccountOne, 1, Instrument, Side.Bid, 2,
                ulong.MaxValue);

            Assert.Equal(RiskRejectReason.ArithmeticOverflow, decision.Reason);
            Assert.Equal(0, risk.ActiveOrders);
            AssertHealthy(risk);
        }

        [Fact]
        public void PositionLimitsReserveEachDirectionalWorstCase()
        {
            var risk = new PreTradeRiskEngine();
            risk.ConfigureAccount(AccountOne, new RiskLimits(
                1_000, 1_000_000, 2_000, 2_000_000, 100, 20));
            Assert.True(risk.SetPosition(AccountOne, Instrument, 10).Accepted);

            Assert.True(risk.Reserve(AccountOne, 1, Instrument, Side.Bid, 60, 1).Accepted);
            Assert.True(risk.Reserve(AccountOne, 2, Instrument, Side.Ask, 100, 1).Accepted);
            Assert.Equal(RiskRejectReason.PositionLimitExceeded,
                risk.Reserve(AccountOne, 3, Instrument, Side.Bid, 31, 1).Reason);
            Assert.Equal(RiskRejectReason.PositionLimitExceeded,
                risk.Reserve(AccountOne, 4, Instrument, Side.Ask, 11, 1).Reason);

            Assert.True(risk.TryApplyFill(1, 20));
            var snapshot = Snapshot(risk, AccountOne);
            Assert.Equal(30, snapshot.Position);
            Assert.Equal(40UL, snapshot.OpenBidQuantity);
            Assert.Equal(100UL, snapshot.OpenAskQuantity);
            AssertHealthy(risk);
        }

        [Fact]
        public void FillAndReleaseAccountingIsExact()
        {
            var risk = new PreTradeRiskEngine();
            risk.ConfigureAccount(AccountOne, RiskLimits.Unbounded);
            Assert.True(risk.Reserve(AccountOne, 1, Instrument, Side.Bid, 100, 5).Accepted);

            Assert.True(risk.TryApplyFill(1, 40));
            Assert.True(risk.TryRelease(1, 20));

            var partial = Snapshot(risk, AccountOne);
            Assert.Equal(40, partial.Position);
            Assert.Equal(40UL, partial.OpenQuantity);
            Assert.Equal(200UL, partial.OpenNotional);
            Assert.Equal(1, partial.ActiveOrders);

            Assert.True(risk.TryApplyFill(1, 40));
            Assert.False(risk.TryApplyFill(1, 1));

            var complete = Snapshot(risk, AccountOne);
            Assert.Equal(80, complete.Position);
            Assert.Equal(0UL, complete.OpenQuantity);
            Assert.Equal(0UL, complete.OpenNotional);
            Assert.Equal(0, complete.ActiveOrders);
            AssertHealthy(risk);
        }

        [Fact]
        public void OpenLimitsAggregateAcrossInstrumentsWhilePositionsRemainSeparate()
        {
            var risk = new PreTradeRiskEngine();
            risk.ConfigureAccount(AccountOne, new RiskLimits(
                100, 10_000, 100, 10_000, 60, 10));

            Assert.True(risk.Reserve(AccountOne, 1, Instrument, Side.Bid, 60, 1).Accepted);
            Assert.Equal(RiskRejectReason.OpenQuantityExceeded,
                risk.Reserve(AccountOne, 2, Instrument + 1, Side.Ask, 41, 1).Reason);
            Assert.True(risk.Reserve(AccountOne, 2, Instrument + 1, Side.Ask, 40, 1).Accepted);
            Assert.True(risk.TryApplyFill(1, 60));

            Assert.True(risk.TryGetAccount(AccountOne, Instrument, out var first));
            Assert.True(risk.TryGetAccount(AccountOne, Instrument + 1, out var second));
            Assert.Equal(60, first.Position);
            Assert.Equal(0, second.Position);
            Assert.Equal(40UL, first.OpenQuantity);
            Assert.Equal(40UL, second.OpenQuantity);
            Assert.Equal(1, first.ActiveOrders);
            Assert.Equal(1, second.ActiveOrders);
            AssertHealthy(risk);
        }

        [Fact]
        public void TighterConfigurationCannotInvalidateLiveExposure()
        {
            var risk = new PreTradeRiskEngine();
            risk.ConfigureAccount(AccountOne, RiskLimits.Unbounded);
            Assert.True(risk.SetPosition(AccountOne, Instrument, 25).Accepted);
            Assert.True(risk.Reserve(AccountOne, 1, Instrument, Side.Bid, 50, 10).Accepted);

            Assert.Throws<InvalidOperationException>(() => risk.ConfigureAccount(AccountOne,
                new RiskLimits(40, 1_000, 1_000, 10_000, 1_000, 10)));
            Assert.Throws<InvalidOperationException>(() => risk.ConfigureAccount(AccountOne,
                new RiskLimits(100, 1_000, 1_000, 10_000, 70, 10)));

            AssertHealthy(risk);
        }

        [Fact]
        public void RiskRejectedOrderNeverTouchesTheBookOrPublicFeed()
        {
            var (book, risk) = NewBook(new RiskLimits(10, 1_000, 100, 10_000, 100, 10),
                AccountOne);
            var events = new List<MarketEvent>();

            var result = book.Submit(AccountOne, 1, Side.Bid, OrderType.Limit,
                TimeInForce.GoodTilCancel, 9, 11, events);

            Assert.True(result.Rejected);
            Assert.Equal(RiskRejectReason.OrderQuantityTooLarge, result.Reservation.Reason);
            Assert.Empty(events);
            Assert.Equal(0, book.OrderCount);
            Assert.Equal(0, risk.ActiveOrders);
            AssertHealthy(risk);
        }

        [Fact]
        public void TradesUpdateBothAccountsAndReleaseAggressorRemainders()
        {
            var (book, risk) = NewBook(RiskLimits.Unbounded, AccountOne, AccountTwo);

            Assert.False(book.Submit(AccountOne, 1, Side.Ask, OrderType.Limit,
                TimeInForce.GoodTilCancel, 10, 100, null).Rejected);
            var events = new List<MarketEvent>();
            var trade = book.Submit(AccountTwo, 2, Side.Bid, OrderType.Limit,
                TimeInForce.ImmediateOrCancel, 10, 60, events);

            Assert.False(trade.Rejected);
            Assert.Equal(60u, trade.Matching.FilledQuantity);
            Assert.Single(events, e => e.Type == MarketEventType.Traded);

            var seller = Snapshot(risk, AccountOne);
            Assert.Equal(-60, seller.Position);
            Assert.Equal(40UL, seller.OpenAskQuantity);
            Assert.Equal(1, seller.ActiveOrders);

            var buyer = Snapshot(risk, AccountTwo);
            Assert.Equal(60, buyer.Position);
            Assert.Equal(0UL, buyer.OpenQuantity);
            Assert.Equal(0, buyer.ActiveOrders);
            Assert.Equal(40u, book.Find(1)?.Remaining);
            AssertHealthy(risk);
        }

        [Fact]
        public void CancelAndReduceRequireOwnershipAndReleaseReservations()
        {
            var (book, risk) = NewBook(RiskLimits.Unbounded, AccountOne, AccountTwo);
            book.Submit(AccountOne, 1, Side.Bid, OrderType.Limit,
                TimeInForce.GoodTilCancel, 9, 100, null);

            Assert.Equal(OrderActionResult.NotOwner, book.Cancel(AccountTwo, 1, null));
            Assert.Equal(OrderActionResult.NotOwner, book.Reduce(AccountTwo, 1, 50, null));
            Assert.Equal(OrderActionResult.InvalidQuantity,
                book.Reduce(AccountOne, 1, 101, null));

            Assert.Equal(OrderActionResult.Applied, book.Reduce(AccountOne, 1, 40, null));
            Assert.Equal(40UL, Snapshot(risk, AccountOne).OpenQuantity);
            Assert.Equal(40u, book.Find(1)?.Remaining);

            Assert.Equal(OrderActionResult.Applied, book.Cancel(AccountOne, 1, null));
            Assert.Equal(OrderActionResult.UnknownOrder, book.Cancel(AccountOne, 1, null));
            Assert.Equal(0, book.OrderCount);
            Assert.Equal(0, risk.ActiveOrders);
            AssertHealthy(risk);
        }

        [Fact]
        public void AccountKillCancelsInDeterministicOrderAndBlocksEntry()
        {
            var (book, risk) = NewBook(RiskLimits.Unbounded, AccountOne, AccountTwo);
            book.Submit(AccountOne, 5, Side.Bid, OrderType.Limit,
                TimeInForce.GoodTilCancel, 8, 10, null);
            book.Submit(AccountTwo, 3, Side.Ask, OrderType.Limit,
                TimeInForce.GoodTilCancel, 12, 10, null);
            book.Submit(AccountOne, 2, Side.Bid, OrderType.Limit,
                TimeInForce.GoodTilCancel, 9, 10, null);
            var events = new List<MarketEvent>();

            Assert.True(book.SetAccountKill(AccountOne, killed: true, cancelResting: true, events));

            Assert.Equal(new ulong[] { 2, 5 }, events.Select(e => e.OrderId));
            Assert.Null(book.Find(2));
            Assert.Null(book.Find(5));
            Assert.NotNull(book.Find(3));
            Assert.Equal(RiskRejectReason.KillSwitchEngaged,
                book.Submit(AccountOne, 6, Side.Bid, OrderType.Limit,
                    TimeInForce.GoodTilCancel, 9, 1, null).Reservation.Reason);

            Assert.True(book.SetAccountKill(AccountOne, killed: false,
                cancelResting: false, events: null));
            Assert.False(book.Submit(AccountOne, 6, Side.Bid, OrderType.Limit,
                TimeInForce.GoodTilCancel, 9, 1, null).Rejected);
            AssertHealthy(risk);
        }

        [Fact]
        public void GlobalKillCancelsTheInstrumentAndFailsClosed()
        {
            var (book, risk) = NewBook(RiskLimits.Unbounded, AccountOne, AccountTwo);
            book.Submit(AccountOne, 1, Side.Bid, OrderType.Limit,
                TimeInForce.GoodTilCancel, 8, 10, null);
            book.Submit(AccountTwo, 2, Side.Ask, OrderType.Limit,
                TimeInForce.GoodTilCancel, 12, 10, null);

            book.SetGlobalKill(killed: true, cancelResting: true, events: null);

            Assert.Equal(0, book.OrderCount);
            Assert.Equal(0, risk.ActiveOrders);
            Assert.Equal(RiskRejectReason.KillSwitchEngaged,
                book.Submit(AccountOne, 3, Side.Bid, OrderType.Limit,
                    TimeInForce.GoodTilCancel, 9, 1, null).Reservation.Reason);
            AssertHealthy(risk);
        }

        [Fact]
        public void PolicyAndExecutionLedgersCloseTogetherAcrossTradeAndCancel()
        {
            var (book, risk, gate) = NewCompositeBook();

            Assert.False(book.Submit(AccountOne, 1, Side.Ask, OrderType.Limit,
                TimeInForce.GoodTilCancel, 10, 100, null).Rejected);
            Assert.Equal(1_000, gate.StateOf("P1").ReservedCredit);

            var trade = book.Submit(AccountTwo, 2, Side.Bid, OrderType.Limit,
                TimeInForce.ImmediateOrCancel, 10, 60, null);

            Assert.False(trade.Rejected);
            Assert.Equal(-60, gate.StateOf("P1").PositionIn(Instrument));
            Assert.Equal(60, gate.StateOf("P2").PositionIn(Instrument));
            Assert.Equal(400, gate.StateOf("P1").ReservedCredit);
            Assert.Equal(0, gate.StateOf("P2").ReservedCredit);
            Assert.Equal(-60, Snapshot(risk, AccountOne).Position);
            Assert.Equal(60, Snapshot(risk, AccountTwo).Position);

            Assert.Equal(OrderActionResult.Applied, book.Cancel(AccountOne, 1, null));
            Assert.Equal(0, gate.StateOf("P1").ReservedCredit);
            Assert.Equal(0, risk.ActiveOrders);
            AssertHealthy(risk);
        }

        [Fact]
        public void PolicyRejectionIsAtomicWithRespectToExecution()
        {
            var executionLimits = new RiskLimits(10, 10_000, 100, 100_000, 100, 10);
            var policyLimits = new ParticipantLimits(
                MaxOrderQuantity: 10, MaxOrderNotional: 10_000,
                MaxNetPosition: 100, CreditLimit: 100_000,
                MaxMessagesPerSecond: int.MaxValue, CollarBasisPoints: 0);
            var (book, risk, gate) = NewCompositeBook(executionLimits, policyLimits);

            var result = book.Submit(AccountOne, 1, Side.Bid, OrderType.Limit,
                TimeInForce.GoodTilCancel, 9, 11, null);

            Assert.True(result.Rejected);
            Assert.Equal(RiskRejectReason.OrderQuantityTooLarge, result.Policy.Reason);
            Assert.True(result.Reservation.Accepted);
            Assert.Equal(0, book.OrderCount);
            Assert.Equal(0, risk.ActiveOrders);
            Assert.Equal(0, gate.StateOf("P1").ReservedCredit);
        }

        [Fact]
        public void ExecutionRejectionRollsBackPolicyCredit()
        {
            var executionLimits = new RiskLimits(10, 10_000, 100, 100_000, 100, 10);
            var policyLimits = new ParticipantLimits(
                MaxOrderQuantity: 100, MaxOrderNotional: 10_000,
                MaxNetPosition: 100, CreditLimit: 100_000,
                MaxMessagesPerSecond: int.MaxValue, CollarBasisPoints: 0);
            var (book, risk, gate) = NewCompositeBook(executionLimits, policyLimits);

            var result = book.Submit(AccountOne, 1, Side.Bid, OrderType.Limit,
                TimeInForce.GoodTilCancel, 9, 11, null);

            Assert.True(result.Policy.IsAccepted);
            Assert.Equal(RiskRejectReason.OrderQuantityTooLarge,
                result.Reservation.Reason);
            Assert.Equal(0, gate.StateOf("P1").ReservedCredit);
            Assert.Equal(0, risk.ActiveOrders);
            Assert.Equal(0, book.OrderCount);
        }

        [Fact]
        public void MarketOrdersReserveAgainstTheFullPriceBand()
        {
            var executionLimits = new RiskLimits(100, 10_000, 1_000, 1_000, 1_000, 100);
            var policyLimits = new ParticipantLimits(
                MaxOrderQuantity: 100, MaxOrderNotional: 10_000,
                MaxNetPosition: 1_000, CreditLimit: 1_000,
                MaxMessagesPerSecond: int.MaxValue, CollarBasisPoints: 0);
            var (book, risk, gate) = NewCompositeBook(executionLimits, policyLimits);

            var result = book.Submit(AccountOne, 1, Side.Bid, OrderType.Market,
                TimeInForce.ImmediateOrCancel, 0, 21, null);

            Assert.True(result.Rejected);
            Assert.Equal(RiskRejectReason.InsufficientCredit, result.Policy.Reason);
            Assert.Equal(0, gate.StateOf("P1").ReservedCredit);
            Assert.Equal(0, risk.ActiveOrders);
        }

        [Fact]
        public void CompositeAccountKillCancelsCreditAndBothRiskStages()
        {
            var (book, risk, gate) = NewCompositeBook();
            Assert.False(book.Submit(AccountOne, 1, Side.Bid, OrderType.Limit,
                TimeInForce.GoodTilCancel, 9, 10, null).Rejected);

            Assert.True(book.SetAccountKill(AccountOne, killed: true,
                cancelResting: true, events: null));

            Assert.True(gate.StateOf("P1").IsKilled);
            Assert.Equal(0, gate.StateOf("P1").ReservedCredit);
            Assert.Equal(0, risk.ActiveOrders);
            var rejected = book.Submit(AccountOne, 2, Side.Bid, OrderType.Limit,
                TimeInForce.GoodTilCancel, 9, 1, null);
            Assert.Equal(RiskRejectReason.KillSwitchEngaged, rejected.Policy.Reason);

            Assert.True(book.SetAccountKill(AccountOne, killed: false,
                cancelResting: false, events: null));
            Assert.False(book.Submit(AccountOne, 2, Side.Bid, OrderType.Limit,
                TimeInForce.GoodTilCancel, 9, 1, null).Rejected);
            AssertHealthy(risk);
        }

        [Fact]
        public void SelfTradePreventionRejectsWithoutLeakingPolicyCredit()
        {
            var (book, risk, gate) = NewCompositeBook();
            Assert.False(book.Submit(AccountOne, 1, Side.Ask, OrderType.Limit,
                TimeInForce.GoodTilCancel, 10, 10, null).Rejected);

            var result = book.Submit(AccountOne, 2, Side.Bid, OrderType.Limit,
                TimeInForce.ImmediateOrCancel, 10, 1, null);

            Assert.Equal(RiskRejectReason.SelfTrade, result.Reservation.Reason);
            Assert.Equal(100, gate.StateOf("P1").ReservedCredit);
            Assert.Equal(1, risk.ActiveOrders);
            Assert.Equal(10u, book.Find(1)?.Remaining);
            Assert.Equal(0, gate.StateOf("P1").PositionIn(Instrument));
            AssertHealthy(risk);
        }

        [Fact]
        public void SelfTradeScanStopsWhenEarlierLiquidityFillsTheOrder()
        {
            var (book, risk) = NewBook(RiskLimits.Unbounded, AccountOne, AccountTwo);
            Assert.False(book.Submit(AccountTwo, 1, Side.Ask, OrderType.Limit,
                TimeInForce.GoodTilCancel, 10, 10, null).Rejected);
            Assert.False(book.Submit(AccountOne, 2, Side.Ask, OrderType.Limit,
                TimeInForce.GoodTilCancel, 10, 10, null).Rejected);

            var result = book.Submit(AccountOne, 3, Side.Bid, OrderType.Limit,
                TimeInForce.ImmediateOrCancel, 10, 10, null);

            Assert.False(result.Rejected);
            Assert.Equal(10u, result.Matching.FilledQuantity);
            Assert.Equal(10u, book.Find(2)?.Remaining);
            Assert.Equal(10, Snapshot(risk, AccountOne).Position);
            AssertHealthy(risk);
        }

        [Fact]
        public void RandomizedCommandFlowPreservesBookRiskAndPositionInvariants()
        {
            for (var seed = 1; seed <= 20; seed++)
            {
                var accounts = new[] { 11UL, 22UL, 33UL, 44UL };
                var (book, risk) = NewBook(new RiskLimits(
                    1_000, 1_000_000, 100_000, 100_000_000, 1_000_000, 10_000),
                    accounts);
                var random = new Random(seed);
                ulong nextId = 1;

                for (var step = 0; step < 1_000; step++)
                {
                    var active = risk.ActiveOrderIds(Instrument);
                    var roll = random.Next(100);

                    if (active.Length != 0 && roll < 25)
                    {
                        var id = active[random.Next(active.Length)];
                        Assert.True(risk.TryGetReservation(id, out var reservation));
                        book.Cancel(reservation.AccountId, id, null);
                    }
                    else if (active.Length != 0 && roll < 40)
                    {
                        var id = active[random.Next(active.Length)];
                        Assert.True(risk.TryGetReservation(id, out var reservation));
                        var quantity = (uint)random.Next(0,
                            checked((int)reservation.RemainingQuantity + 1));
                        book.Reduce(reservation.AccountId, id, quantity, null);
                    }
                    else
                    {
                        var account = accounts[random.Next(accounts.Length)];
                        var side = random.Next(2) == 0 ? Side.Bid : Side.Ask;
                        var typeRoll = random.Next(100);
                        var type = typeRoll < 10 ? OrderType.Market
                            : typeRoll < 18 ? OrderType.MarketToLimit
                            : OrderType.Limit;
                        var timeInForce = type == OrderType.MarketToLimit
                            ? TimeInForce.GoodTilCancel
                            : random.Next(100) switch
                            {
                                < 15 => TimeInForce.ImmediateOrCancel,
                                < 25 => TimeInForce.FillOrKill,
                                < 40 when type == OrderType.Limit => TimeInForce.GoodTilCrossing,
                                _ => TimeInForce.GoodTilCancel,
                            };

                        book.Submit(account, nextId++, side, type, timeInForce,
                            random.Next(-50, 51), (uint)random.Next(1, 200), null);
                    }

                    AssertHealthy(risk, seed, step);
                    Assert.Equal(book.OrderCount, risk.ActiveOrders);

                    foreach (var side in new[] { Side.Bid, Side.Ask })
                    {
                        foreach (var order in book.OrdersInPriority(side))
                        {
                            Assert.True(risk.TryGetReservation(order.Id, out var reservation));
                            Assert.Equal(order.Remaining, reservation.RemainingQuantity);
                            Assert.Equal(side, reservation.Side);
                        }
                    }

                    long netPosition = 0;
                    foreach (var account in accounts)
                        netPosition += Snapshot(risk, account).Position;
                    Assert.Equal(0, netPosition);
                }
            }
        }

        private static (RiskManagedOrderBook Book, PreTradeRiskEngine Risk) NewBook(
            RiskLimits limits, params ulong[] accounts)
        {
            var risk = new PreTradeRiskEngine();
            foreach (var account in accounts)
                risk.ConfigureAccount(account, limits);
            return (new RiskManagedOrderBook(Instrument, -50, 50, risk), risk);
        }

        private static (RiskManagedOrderBook Book, PreTradeRiskEngine Risk,
            PreTradeRiskGate Gate) NewCompositeBook(
                RiskLimits? executionLimits = null, ParticipantLimits? policyLimits = null)
        {
            var risk = new PreTradeRiskEngine();
            var limits = executionLimits ?? new RiskLimits(
                1_000, 1_000_000, 10_000, 1_000_000, 1_000, 1_000);
            risk.ConfigureAccount(AccountOne, limits);
            risk.ConfigureAccount(AccountTwo, limits);

            var policy = policyLimits ?? new ParticipantLimits(
                MaxOrderQuantity: 1_000, MaxOrderNotional: 1_000_000,
                MaxNetPosition: 1_000, CreditLimit: 1_000_000,
                MaxMessagesPerSecond: int.MaxValue, CollarBasisPoints: 0);
            var gate = new PreTradeRiskGate();
            gate.Register("P1", policy);
            gate.Register("P2", policy);
            gate.GrantAll("P1", Entitlement.All);
            gate.GrantAll("P2", Entitlement.All);

            var book = new RiskManagedOrderBook(Instrument, -50, 50, risk, gate);
            book.BindAccount(AccountOne, "P1");
            book.BindAccount(AccountTwo, "P2");
            return (book, risk, gate);
        }

        private static RiskAccountSnapshot Snapshot(PreTradeRiskEngine risk, ulong accountId)
        {
            Assert.True(risk.TryGetAccount(accountId, Instrument, out var snapshot));
            return snapshot;
        }

        private static void AssertHealthy(PreTradeRiskEngine risk, int seed = 0, int step = 0)
        {
            Assert.True(risk.Validate(out var error),
                $"risk invariant failed at seed {seed}, step {step}: {error}");
        }
    }
}
