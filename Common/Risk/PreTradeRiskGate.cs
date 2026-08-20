using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using MarketData.Common.Books;
using MarketData.Common.Matching;
using MarketData.Common.Reference;

namespace MarketData.Common.Risk
{
    /// <summary>What a participant may do with an instrument.</summary>
    [Flags]
    public enum Entitlement
    {
        None = 0,

        /// <summary>May receive market data.</summary>
        Subscribe = 1,

        /// <summary>May send orders.</summary>
        Trade = 2,

        /// <summary>May receive a drop copy of its own activity.</summary>
        DropCopy = 4,

        All = Subscribe | Trade | DropCopy,
    }

    /// <summary>An order as the gate sees it, before the book does.</summary>
    public readonly record struct OrderRequest(
        string ParticipantId,
        int InstrumentId,
        Side Side,
        int Price,
        uint Quantity,
        OrderType Type = OrderType.Limit)
    {
        /// <summary>Signed quantity: positive buys, negative sells.</summary>
        public long SignedQuantity => Side == Side.Bid ? Quantity : -(long)Quantity;

        public long Notional
        {
            get
            {
                var value = (Int128)Price * Quantity;
                return (long)(value < 0 ? -value : value);
            }
        }
    }

    /// <summary>
    /// Every check an order passes before it reaches the book.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Order matters and is not arbitrary. Cheap, categorical refusals come first - entitlement,
    /// kill switch, session state - because they cost a lookup and reject the largest share of bad
    /// traffic. Reservations come last, because a reservation that passes must be released if a
    /// later check fails, and every check placed after one is another release path to get wrong.
    /// </para>
    /// <para>
    /// The gate is allocation-free on the accept path, which <c>AllocationTests</c> asserts. That
    /// is not decoration: this runs per order, and a gate that allocates hands the collector work
    /// proportional to message rate, with the resulting pauses landing in the one place that
    /// cannot absorb them.
    /// </para>
    /// </remarks>
    public sealed class PreTradeRiskGate
    {
        private readonly ConcurrentDictionary<string, ParticipantRiskState> _participants = new();
        private readonly ConcurrentDictionary<(string, int), Entitlement> _entitlements = new();
        private readonly ConcurrentDictionary<string, Entitlement> _defaultEntitlements = new();
        private readonly ConcurrentDictionary<int, int> _referencePrices = new();
        private readonly InstrumentMaster _reference;
        private readonly SessionCalendar _calendar;
        private readonly Func<DateTime> _clock;

        private int _globalKill;
        private long _accepted;
        private long _rejected;

        public PreTradeRiskGate(
            InstrumentMaster reference = null,
            SessionCalendar calendar = null,
            Func<DateTime> clock = null)
        {
            _reference = reference;
            _calendar = calendar;
            _clock = clock ?? (() => DateTime.UtcNow);
        }

        /// <summary>Engaged, everything stops. The venue-wide stop.</summary>
        public bool GlobalKillEngaged => Volatile.Read(ref _globalKill) != 0;

        public long Accepted => Interlocked.Read(ref _accepted);
        public long Rejected => Interlocked.Read(ref _rejected);

        public event Action<OrderRequest, RiskDecision> Rejection;

        public ParticipantRiskState Register(string participantId, ParticipantLimits limits = null)
        {
            if (string.IsNullOrWhiteSpace(participantId))
                throw new ArgumentException("A participant id is required.", nameof(participantId));

            return _participants.GetOrAdd(participantId,
                id => new ParticipantRiskState(id, limits ?? ParticipantLimits.Default));
        }

        public ParticipantRiskState StateOf(string participantId)
            => !string.IsNullOrEmpty(participantId) &&
                _participants.TryGetValue(participantId, out var state) ? state : null;

        public void Grant(string participantId, int instrumentId, Entitlement entitlement)
            => _entitlements[(participantId, instrumentId)] = entitlement;

        public void GrantAll(string participantId, Entitlement entitlement)
            => _defaultEntitlements[participantId] = entitlement;

        public void Revoke(string participantId, int instrumentId)
            => _entitlements.TryRemove((participantId, instrumentId), out _);

        public Entitlement EntitlementFor(string participantId, int instrumentId)
        {
            if (_entitlements.TryGetValue((participantId, instrumentId), out var specific))
                return specific;

            return _defaultEntitlements.TryGetValue(participantId, out var blanket)
                ? blanket
                : Entitlement.None;
        }

        public bool IsEntitled(string participantId, int instrumentId, Entitlement required)
            => (EntitlementFor(participantId, instrumentId) & required) == required;

        /// <summary>Sets the price the collar is measured against.</summary>
        public void SetReferencePrice(int instrumentId, int price) => _referencePrices[instrumentId] = price;

        /// <summary>Stops the venue. Deliberately has no automatic counterpart.</summary>
        public bool EngageGlobalKill() => Interlocked.Exchange(ref _globalKill, 1) == 0;

        public bool ReleaseGlobalKill() => Interlocked.Exchange(ref _globalKill, 0) == 1;

        /// <summary>Runs every pre-trade check.</summary>
        public RiskDecision Check(in OrderRequest order) => Check(order, order.Notional);

        /// <summary>
        /// Runs every check while reserving a caller-supplied conservative notional.
        /// The submitted price still drives tick and collar validation.
        /// </summary>
        public RiskDecision Check(in OrderRequest order, long reservedNotional)
        {
            var decision = Evaluate(order, reservedNotional);

            if (decision.IsAccepted)
            {
                Interlocked.Increment(ref _accepted);
                StateOf(order.ParticipantId)?.RecordAccepted();
            }
            else
            {
                Interlocked.Increment(ref _rejected);
                StateOf(order.ParticipantId)?.RecordRejected();
                Rejection?.Invoke(order, decision);
            }

            return decision;
        }

        private RiskDecision Evaluate(in OrderRequest order, long reservedNotional)
        {
            if (GlobalKillEngaged)
                return RiskDecision.Reject(RiskRejectReason.KillSwitchEngaged);

            if (string.IsNullOrEmpty(order.ParticipantId) || order.InstrumentId <= 0 ||
                (byte)order.Side > (byte)Side.Ask ||
                (byte)order.Type > (byte)OrderType.MarketToLimit || reservedNotional < 0)
                return RiskDecision.Reject(RiskRejectReason.InvalidOrder);

            if (!_participants.TryGetValue(order.ParticipantId, out var state))
                return RiskDecision.Reject(RiskRejectReason.NotEntitled);

            if (state.IsKilled)
                return RiskDecision.Reject(RiskRejectReason.KillSwitchEngaged);

            if (!IsEntitled(order.ParticipantId, order.InstrumentId, Entitlement.Trade))
                return RiskDecision.Reject(RiskRejectReason.NotEntitled, order.InstrumentId);

            if (_calendar is not null && !_calendar.IsContinuousTrading(_clock(), order.InstrumentId))
                return RiskDecision.Reject(RiskRejectReason.SessionClosed);

            if (order.Quantity == 0 || order.Quantity > state.Limits.MaxOrderQuantity)
                return RiskDecision.Reject(RiskRejectReason.OrderQuantityTooLarge, state.Limits.MaxOrderQuantity);

            if (reservedNotional > state.Limits.MaxOrderNotional)
                return RiskDecision.Reject(RiskRejectReason.OrderNotionalTooLarge, state.Limits.MaxOrderNotional);

            var priceValidated = order.Type == OrderType.Limit;

            if (priceValidated && _reference is not null)
            {
                var record = _reference.AsOf(order.InstrumentId, _clock());

                if (record is not null)
                {
                    if (record.TickSize > 0 && order.Price % record.TickSize != 0)
                        return RiskDecision.Reject(RiskRejectReason.PriceNotOnTick, record.TickSize);

                    if (record.LotSize > 0 && order.Quantity % record.LotSize != 0)
                        return RiskDecision.Reject(RiskRejectReason.QuantityNotOnLot, record.LotSize);
                }
            }

            if (priceValidated && state.Limits.CollarBasisPoints > 0 &&
                _referencePrices.TryGetValue(order.InstrumentId, out var referencePrice) &&
                referencePrice > 0)
            {
                var allowed = (long)referencePrice * state.Limits.CollarBasisPoints / 10_000;
                var distance = Math.Abs((long)order.Price - referencePrice);

                if (distance > allowed)
                    return RiskDecision.Reject(RiskRejectReason.PriceOutsideCollar, allowed);
            }

            if (!state.WouldStayWithinPositionLimit(order.InstrumentId, order.SignedQuantity))
                return RiskDecision.Reject(RiskRejectReason.PositionLimitExceeded, state.Limits.MaxNetPosition);

            // Rate is consumed before credit is reserved, so a throttled participant never leaves
            // a reservation behind that something else has to remember to release.
            if (!state.TryConsumeRateToken())
                return RiskDecision.Reject(RiskRejectReason.RateLimitExceeded, state.Limits.MaxMessagesPerSecond);

            if (!state.TryReserveCredit(reservedNotional))
                return RiskDecision.Reject(RiskRejectReason.InsufficientCredit, state.AvailableCredit);

            return RiskDecision.Accepted;
        }

        /// <summary>Releases what an accepted order reserved, when it leaves the book.</summary>
        public void OnOrderClosed(in OrderRequest order)
            => StateOf(order.ParticipantId)?.ReleaseCredit(order.Notional);

        public void OnOrderClosed(string participantId, long reservedNotional)
            => StateOf(participantId)?.ReleaseCredit(reservedNotional);

        /// <summary>Applies a fill to the participant's position.</summary>
        public void OnFill(string participantId, int instrumentId, long signedQuantity, long notional)
        {
            if (_participants.TryGetValue(participantId, out var state))
                state.ApplyFillAndReleaseCredit(instrumentId, signedQuantity, notional);
        }

        /// <summary>Every participant currently killed, for a status page or an alert.</summary>
        public IReadOnlyList<string> KilledParticipants()
        {
            var killed = new List<string>();

            foreach (var (id, state) in _participants)
            {
                if (state.IsKilled)
                    killed.Add(id);
            }

            return killed;
        }
    }
}
