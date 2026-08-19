using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MarketData.Common.Books;
using MarketData.Common.Durability;
using MarketData.Common.Reference;
using MarketData.Common.Risk;
using Xunit;

namespace MarketData.Tests
{
    public class PreTradeRiskTests
    {
        private const string Trader = "TRADER-1";
        private const int Instrument = 7;

        private static PreTradeRiskGate Gate(ParticipantLimits limits = null,
            InstrumentMaster reference = null, SessionCalendar calendar = null, Func<DateTime> clock = null)
        {
            var gate = new PreTradeRiskGate(reference, calendar, clock);
            gate.Register(Trader, limits ?? ParticipantLimits.Default);
            gate.GrantAll(Trader, Entitlement.All);
            return gate;
        }

        private static OrderRequest Order(uint quantity = 100, int price = 1000, Side side = Side.Bid)
            => new(Trader, Instrument, side, price, quantity);

        [Fact]
        public void AWellFormedOrderIsAccepted()
            => Assert.True(Gate().Check(Order()).IsAccepted);

        [Fact]
        public void AnUnknownParticipantIsRefused()
        {
            var gate = new PreTradeRiskGate();
            var decision = gate.Check(new OrderRequest("NOBODY", Instrument, Side.Bid, 100, 1));

            Assert.Equal(RiskRejectReason.NotEntitled, decision.Reason);
        }

        [Fact]
        public void TradingWithoutTheEntitlementIsRefused()
        {
            var gate = new PreTradeRiskGate();
            gate.Register(Trader);
            gate.GrantAll(Trader, Entitlement.Subscribe);   // data only

            Assert.Equal(RiskRejectReason.NotEntitled, gate.Check(Order()).Reason);

            gate.Grant(Trader, Instrument, Entitlement.Subscribe | Entitlement.Trade);
            Assert.True(gate.Check(Order()).IsAccepted);
        }

        [Fact]
        public void AnEntitlementCanBeRevoked()
        {
            var gate = new PreTradeRiskGate();
            gate.Register(Trader);
            gate.Grant(Trader, Instrument, Entitlement.All);
            Assert.True(gate.Check(Order()).IsAccepted);

            gate.Revoke(Trader, Instrument);
            Assert.Equal(RiskRejectReason.NotEntitled, gate.Check(Order()).Reason);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(100_001)]
        public void OversizedOrdersAreRefused(uint quantity)
            => Assert.Equal(RiskRejectReason.OrderQuantityTooLarge,
                Gate(new ParticipantLimits(MaxOrderQuantity: 100_000)).Check(Order(quantity)).Reason);

        [Fact]
        public void NotionalIsCappedIndependentlyOfQuantity()
        {
            var gate = Gate(new ParticipantLimits(MaxOrderQuantity: 1_000_000, MaxOrderNotional: 1_000_000));

            Assert.True(gate.Check(Order(quantity: 100, price: 10_000)).IsAccepted);
            Assert.Equal(RiskRejectReason.OrderNotionalTooLarge,
                gate.Check(Order(quantity: 101, price: 10_000)).Reason);
        }

        [Fact]
        public void PricesOutsideTheCollarAreRefused()
        {
            var gate = Gate(new ParticipantLimits(CollarBasisPoints: 500)); // 5%
            gate.SetReferencePrice(Instrument, 1_000);

            Assert.True(gate.Check(Order(price: 1_050)).IsAccepted);
            Assert.True(gate.Check(Order(price: 950)).IsAccepted);
            Assert.Equal(RiskRejectReason.PriceOutsideCollar, gate.Check(Order(price: 1_051)).Reason);
            Assert.Equal(RiskRejectReason.PriceOutsideCollar, gate.Check(Order(price: 949)).Reason);
        }

        [Fact]
        public void ACollarOfZeroDisablesTheCheck()
        {
            var gate = Gate(new ParticipantLimits(CollarBasisPoints: 0));
            gate.SetReferencePrice(Instrument, 1_000);

            Assert.True(gate.Check(Order(price: 1_000_000)).IsAccepted);
        }

        [Fact]
        public void TickAndLotSizesComeFromReferenceData()
        {
            var reference = new InstrumentMaster();
            reference.Amend(new InstrumentRecord(Instrument, "TST", TickSize: 25, LotSize: 100,
                "USD", DateTime.MinValue, DateTime.MaxValue, DateTime.MinValue,
                ReferenceChangeReason.Listing));

            var gate = Gate(reference: reference);

            Assert.True(gate.Check(Order(quantity: 200, price: 1_000)).IsAccepted);
            Assert.Equal(RiskRejectReason.PriceNotOnTick, gate.Check(Order(quantity: 200, price: 1_010)).Reason);
            Assert.Equal(RiskRejectReason.QuantityNotOnLot, gate.Check(Order(quantity: 150, price: 1_000)).Reason);
        }

        [Fact]
        public void OrdersAreRefusedOutsideContinuousTrading()
        {
            var calendar = SessionCalendar.UsEquities();
            var wednesday = new DateTime(2026, 8, 19);
            var now = wednesday.AddHours(12);

            var gate = Gate(calendar: calendar, clock: () => now);
            Assert.True(gate.Check(Order()).IsAccepted);

            now = wednesday.AddHours(3);   // before the open
            Assert.Equal(RiskRejectReason.SessionClosed, gate.Check(Order()).Reason);
        }

        // ---------------------------------------------------------------- kill switches

        [Fact]
        public void AParticipantKillSwitchStopsThatParticipantOnly()
        {
            var gate = Gate();
            gate.Register("OTHER");
            gate.GrantAll("OTHER", Entitlement.All);

            Assert.True(gate.StateOf(Trader).Kill());
            Assert.False(gate.StateOf(Trader).Kill());   // idempotent

            Assert.Equal(RiskRejectReason.KillSwitchEngaged, gate.Check(Order()).Reason);
            Assert.True(gate.Check(new OrderRequest("OTHER", Instrument, Side.Bid, 1000, 100)).IsAccepted);

            Assert.Contains(Trader, gate.KilledParticipants());

            Assert.True(gate.StateOf(Trader).Revive());
            Assert.True(gate.Check(Order()).IsAccepted);
        }

        [Fact]
        public void TheGlobalKillSwitchStopsEveryone()
        {
            var gate = Gate();
            gate.Register("OTHER");
            gate.GrantAll("OTHER", Entitlement.All);

            Assert.True(gate.EngageGlobalKill());
            Assert.Equal(RiskRejectReason.KillSwitchEngaged, gate.Check(Order()).Reason);
            Assert.Equal(RiskRejectReason.KillSwitchEngaged,
                gate.Check(new OrderRequest("OTHER", Instrument, Side.Bid, 1000, 100)).Reason);

            Assert.True(gate.ReleaseGlobalKill());
            Assert.True(gate.Check(Order()).IsAccepted);
        }

        // ---------------------------------------------------------------- credit

        [Fact]
        public void CreditIsReservedAndReleased()
        {
            var gate = Gate(new ParticipantLimits(CreditLimit: 1_000_000));
            var state = gate.StateOf(Trader);

            var order = Order(quantity: 100, price: 5_000);   // 500,000 notional
            Assert.True(gate.Check(order).IsAccepted);
            Assert.Equal(500_000, state.ReservedCredit);

            // A second identical order exactly exhausts the limit.
            Assert.True(gate.Check(order).IsAccepted);
            Assert.Equal(1_000_000, state.ReservedCredit);
            Assert.Equal(0, state.AvailableCredit);

            Assert.Equal(RiskRejectReason.InsufficientCredit, gate.Check(order).Reason);

            gate.OnOrderClosed(order);
            Assert.Equal(500_000, state.ReservedCredit);
            Assert.True(gate.Check(order).IsAccepted);
        }

        /// <summary>
        /// Concurrent orders must not both pass a check only one of them fits under.
        /// </summary>
        /// <remarks>
        /// The reason reservation is a compare-and-swap rather than check-then-add. Under the naive
        /// shape both threads read the same available credit, both decide they fit, and the limit
        /// is exceeded by exactly the amount the limit existed to prevent.
        /// </remarks>
        [Fact]
        public void ConcurrentOrdersCannotOversubscribeCredit()
        {
            const int threads = 16;
            const int perThread = 500;
            const long notional = 1_000;
            const long limit = threads * perThread * notional / 2;   // room for exactly half

            var gate = Gate(new ParticipantLimits(
                MaxOrderQuantity: 1_000_000,
                MaxOrderNotional: long.MaxValue,
                MaxNetPosition: long.MaxValue,
                CreditLimit: limit,
                MaxMessagesPerSecond: int.MaxValue));

            var state = gate.StateOf(Trader);
            var accepted = 0;

            Parallel.For(0, threads, _ =>
            {
                for (var i = 0; i < perThread; i++)
                {
                    if (gate.Check(new OrderRequest(Trader, Instrument, Side.Bid, 10, 100)).IsAccepted)
                        System.Threading.Interlocked.Increment(ref accepted);
                }
            });

            Assert.True(state.ReservedCredit <= limit,
                $"reserved {state.ReservedCredit} against a limit of {limit}");
            Assert.Equal(limit, state.ReservedCredit);
            Assert.Equal(limit / notional, accepted);
        }

        [Fact]
        public void ReleasingMoreCreditThanWasReservedCannotGrantUnlimitedCapacity()
        {
            var gate = Gate(new ParticipantLimits(CreditLimit: 1_000));
            var state = gate.StateOf(Trader);

            state.ReleaseCredit(1_000_000);

            Assert.Equal(0, state.ReservedCredit);
            Assert.Equal(1_000, state.AvailableCredit);
        }

        // ---------------------------------------------------------------- position and rate

        [Fact]
        public void NetPositionIsCapped()
        {
            var gate = Gate(new ParticipantLimits(MaxNetPosition: 500, MaxOrderQuantity: 1_000));
            var state = gate.StateOf(Trader);

            Assert.True(gate.Check(Order(quantity: 400)).IsAccepted);
            state.ApplyFill(Instrument, 400);

            Assert.Equal(RiskRejectReason.PositionLimitExceeded, gate.Check(Order(quantity: 200)).Reason);

            // The other way reduces the position, so it is fine.
            Assert.True(gate.Check(Order(quantity: 200, side: Side.Ask)).IsAccepted);
        }

        /// <summary>A token bucket bounds the burst, which a fixed window does not.</summary>
        [Fact]
        public void TheRateLimitBoundsBurstsNotJustAverages()
        {
            var gate = Gate(new ParticipantLimits(MaxMessagesPerSecond: 10));

            for (var i = 0; i < 10; i++)
                Assert.True(gate.Check(Order()).IsAccepted, $"order {i} should have fitted the burst");

            Assert.Equal(RiskRejectReason.RateLimitExceeded, gate.Check(Order()).Reason);
        }

        [Fact]
        public void ARefusedOrderLeavesNoCreditReserved()
        {
            var gate = Gate(new ParticipantLimits(MaxOrderQuantity: 50, CreditLimit: 1_000_000));
            var state = gate.StateOf(Trader);

            Assert.False(gate.Check(Order(quantity: 500)).IsAccepted);
            Assert.Equal(0, state.ReservedCredit);
        }

        [Fact]
        public void CountersTrackAcceptanceAndRejection()
        {
            var gate = Gate(new ParticipantLimits(MaxOrderQuantity: 100));

            gate.Check(Order(quantity: 50));
            gate.Check(Order(quantity: 500));

            Assert.Equal(1, gate.Accepted);
            Assert.Equal(1, gate.Rejected);
            Assert.Equal(1, gate.StateOf(Trader).AcceptedOrders);
            Assert.Equal(1, gate.StateOf(Trader).RejectedOrders);
        }

        [Fact]
        public void RejectionsAreObservable()
        {
            var gate = Gate(new ParticipantLimits(MaxOrderQuantity: 10));
            var seen = new List<RiskRejectReason>();
            gate.Rejection += (_, decision) => seen.Add(decision.Reason);

            gate.Check(Order(quantity: 500));

            Assert.Equal(new[] { RiskRejectReason.OrderQuantityTooLarge }, seen);
        }
    }

    public class DropCopyTests
    {
        /// <summary>
        /// A drop copy must never carry another participant's activity.
        /// </summary>
        /// <remarks>
        /// This is a confidentiality property, not a feature. Getting it wrong discloses one firm's
        /// order flow to another, which is materially worse than any bug in the same code.
        /// </remarks>
        [Fact]
        public void AParticipantSeesOnlyItsOwnActivity()
        {
            var service = new DropCopyService();
            var mine = new List<DropCopyEvent>();
            var theirs = new List<DropCopyEvent>();

            using var a = service.Subscribe("A", mine.Add);
            using var b = service.Subscribe("B", theirs.Add);

            service.Publish(new DropCopyEvent(1, DateTime.UtcNow, AuditEventType.OrderAccepted,
                "A", 1, Side.Bid, 100, 10, RiskRejectReason.None));
            service.Publish(new DropCopyEvent(2, DateTime.UtcNow, AuditEventType.Fill,
                "B", 2, Side.Ask, 200, 20, RiskRejectReason.None));

            Assert.Single(mine);
            Assert.Equal("A", mine[0].ParticipantId);
            Assert.Single(theirs);
            Assert.Equal("B", theirs[0].ParticipantId);

            Assert.DoesNotContain(mine, e => e.ParticipantId != "A");
            Assert.DoesNotContain(theirs, e => e.ParticipantId != "B");
        }

        [Fact]
        public void SubscribingWithoutTheEntitlementFailsLoudlyRatherThanSilently()
        {
            var gate = new PreTradeRiskGate();
            gate.Register("A");
            gate.GrantAll("A", Entitlement.Subscribe | Entitlement.Trade);

            var service = new DropCopyService(gate);

            Assert.Throws<InvalidOperationException>(() => service.Subscribe("A", _ => { }));

            gate.GrantAll("A", Entitlement.All);
            using var subscription = service.Subscribe("A", _ => { });
            Assert.NotNull(subscription);
        }

        [Fact]
        public void DisposingStopsDelivery()
        {
            var service = new DropCopyService();
            var seen = new List<DropCopyEvent>();
            var subscription = service.Subscribe("A", seen.Add);

            service.Publish(new DropCopyEvent(1, DateTime.UtcNow, AuditEventType.OrderAccepted,
                "A", 1, Side.Bid, 100, 10, RiskRejectReason.None));
            subscription.Dispose();
            service.Publish(new DropCopyEvent(2, DateTime.UtcNow, AuditEventType.Fill,
                "A", 1, Side.Bid, 100, 10, RiskRejectReason.None));

            Assert.Single(seen);
        }
    }

    public sealed class AuditTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(),
            "mds-audit-" + Guid.NewGuid().ToString("N"));

        public AuditTests() => Directory.CreateDirectory(_root);

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        }

        [Fact]
        public void AuditEventsSurviveARestartAndKeepTheirOrder()
        {
            var sequencer = new Sequencer();

            using (var journal = new WriteAheadJournal(_root, 42, DurabilityPolicy.SyncEachRecord))
            {
                var audit = new AuditLog(journal, sequencer);

                audit.Record(AuditEventType.OrderAccepted, "A", 1);
                audit.Record(AuditEventType.OrderRejected, "B", 2,
                    RiskRejectReason.InsufficientCredit, detail: 12_345);
                audit.Record(AuditEventType.KillSwitchEngaged, "A");

                Assert.Equal(3, audit.EventsWritten);
            }

            var recovered = AuditLog.ReadAll(_root);

            Assert.Equal(3, recovered.Count);
            Assert.Equal(AuditEventType.OrderAccepted, recovered[0].Type);
            Assert.Equal("A", recovered[0].ParticipantId);

            Assert.Equal(AuditEventType.OrderRejected, recovered[1].Type);
            Assert.Equal("B", recovered[1].ParticipantId);
            Assert.Equal(RiskRejectReason.InsufficientCredit, recovered[1].Reason);
            Assert.Equal(12_345, recovered[1].Detail);

            Assert.Equal(AuditEventType.KillSwitchEngaged, recovered[2].Type);

            // Sequence order is the log's order.
            Assert.True(recovered.Select(e => e.Sequence).SequenceEqual(
                recovered.Select(e => e.Sequence).OrderBy(s => s)));
        }

        /// <summary>
        /// Market data and audit share one log, so one sequence orders both.
        /// </summary>
        /// <remarks>
        /// The property that makes "what did the system know and do at sequence N" answerable at
        /// all. Two separate stores would require reconciling two clocks, which is where the
        /// answer goes missing.
        /// </remarks>
        [Fact]
        public void AuditAndMarketDataShareOneOrdering()
        {
            var sequencer = new Sequencer();

            using (var journal = new WriteAheadJournal(_root, 42, DurabilityPolicy.OsBuffered))
            {
                var audit = new AuditLog(journal, sequencer);

                journal.Append(JournalRecordType.Message, sequencer.Next(), 0, new byte[] { 1 });
                audit.Record(AuditEventType.OrderAccepted, "A", 1);
                journal.Append(JournalRecordType.Message, sequencer.Next(), 0, new byte[] { 2 });
            }

            var kinds = new List<JournalRecordType>();

            JournalReader.Recover(_root, (in JournalRecordView record) =>
            {
                if (record.Type is JournalRecordType.Message or JournalRecordType.Audit)
                    kinds.Add(record.Type);
                return true;
            });

            Assert.Equal(new[]
            {
                JournalRecordType.Message,
                JournalRecordType.Audit,
                JournalRecordType.Message,
            }, kinds);
        }

        [Fact]
        public void RetentionNeverDeletesEverything()
        {
            var segmentBytes = JournalRecord.OverheadSize + JournalRecord.MaxPayloadSize;
            var payload = new byte[1024];

            using (var journal = new WriteAheadJournal(_root, 42, DurabilityPolicy.OsBuffered, segmentBytes))
            {
                for (var i = 1; i <= 4_000; i++)
                    journal.Append(JournalRecordType.Audit, (ulong)i, i, payload);
            }

            var before = Directory.GetFiles(_root, "segment-*.jrn").Length;
            Assert.True(before > 2, $"expected several segments, got {before}");

            // Everything is far older than the cutoff, but the floor still holds.
            var removed = new RetentionPolicy(TimeSpan.Zero, MinimumSegments: 2)
                .Enforce(_root, DateTime.UtcNow.AddYears(1));

            var after = Directory.GetFiles(_root, "segment-*.jrn").Length;

            Assert.Equal(before - after, removed);
            Assert.Equal(2, after);
        }

        [Fact]
        public void RetentionKeepsSegmentsInsideTheWindow()
        {
            using (var journal = new WriteAheadJournal(_root, 42, DurabilityPolicy.OsBuffered))
                journal.Append(JournalRecordType.Audit, 1, 0, new byte[] { 1 });

            var removed = RetentionPolicy.SevenYears.Enforce(_root, DateTime.UtcNow);

            Assert.Equal(0, removed);
            Assert.NotEmpty(Directory.GetFiles(_root, "segment-*.jrn"));
        }
    }
}
