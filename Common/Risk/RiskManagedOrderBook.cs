using System;
using System.Collections.Generic;
using MarketData.Common.Books;
using MarketData.Common.Matching;

namespace MarketData.Common.Risk
{
    public readonly record struct OrderEntryResult(
        SubmitResult Matching,
        ReservationDecision Reservation,
        RiskDecision Policy)
    {
        public bool Rejected => Matching.Rejected || !Reservation.Accepted || !Policy.IsAccepted;

        public RiskRejectReason Reason => !Policy.IsAccepted
            ? Policy.Reason
            : !Reservation.Accepted
                ? Reservation.Reason
                : Matching.Rejected ? RiskRejectReason.InvalidOrder : RiskRejectReason.None;
    }

    public enum OrderActionResult : byte
    {
        Applied = 0,
        UnknownOrder,
        NotOwner,
        InvalidQuantity,
    }

    /// <summary>Matching-book command boundary with pre-trade reservation accounting.</summary>
    public sealed class RiskManagedOrderBook
    {
        private readonly LimitOrderBook _book;
        private readonly PreTradeRiskEngine _risk;
        private readonly PreTradeRiskGate _policy;
        private readonly Dictionary<ulong, string> _participants = new();
        private readonly Dictionary<string, ulong> _accountsByParticipant =
            new(StringComparer.Ordinal);
        private readonly List<MarketEvent> _events = new(64);

        public RiskManagedOrderBook(int instrumentId, int minPrice, int maxPrice,
            PreTradeRiskEngine risk, PreTradeRiskGate policy = null)
        {
            if (instrumentId <= 0)
                throw new ArgumentOutOfRangeException(nameof(instrumentId));

            InstrumentId = instrumentId;
            _book = new LimitOrderBook(minPrice, maxPrice);
            _risk = risk ?? throw new ArgumentNullException(nameof(risk));
            _policy = policy;
        }

        public int InstrumentId { get; }
        public int MinPrice => _book.MinPrice;
        public int MaxPrice => _book.MaxPrice;
        public int OrderCount => _book.OrderCount;
        public PreTradeRiskEngine Risk => _risk;

        /// <summary>
        /// Binds a sequencer-local numeric account to the policy gate's authenticated participant.
        /// One participant maps to one account within this book.
        /// </summary>
        public void BindAccount(ulong accountId, string participantId)
        {
            if (_policy is null)
                throw new InvalidOperationException("This book has no policy gate.");
            if (string.IsNullOrWhiteSpace(participantId))
                throw new ArgumentException("A participant id is required.", nameof(participantId));
            if (!_risk.TryGetLimits(accountId, out var executionLimits) ||
                !_risk.TryGetAccount(accountId, InstrumentId, out var execution) ||
                _policy.StateOf(participantId) is not { } participant)
                throw new InvalidOperationException("Both risk stages must be configured before binding.");
            if (execution.ActiveOrders != 0)
                throw new InvalidOperationException("An account with live orders cannot be rebound.");
            if (_accountsByParticipant.TryGetValue(participantId, out var existingAccount) &&
                existingAccount != accountId)
                throw new InvalidOperationException("A participant is already bound to another account.");

            var policyLimits = participant.Limits;
            if (executionLimits.MaxOrderQuantity > policyLimits.MaxOrderQuantity ||
                executionLimits.MaxOrderNotional > (ulong)policyLimits.MaxOrderNotional ||
                executionLimits.MaxOpenNotional > (ulong)policyLimits.CreditLimit ||
                executionLimits.MaxAbsolutePosition > policyLimits.MaxNetPosition ||
                execution.Position != participant.PositionIn(InstrumentId) ||
                execution.OpenNotional != (ulong)participant.ReservedCredit)
                throw new InvalidOperationException("Execution limits or position exceed the policy envelope.");

            if (_participants.TryGetValue(accountId, out var existingParticipant) &&
                !StringComparer.Ordinal.Equals(existingParticipant, participantId))
                _accountsByParticipant.Remove(existingParticipant);

            _participants[accountId] = participantId;
            _accountsByParticipant[participantId] = accountId;
        }

        public OrderEntryResult Submit(ulong accountId, ulong orderId, Side side, OrderType type,
            TimeInForce timeInForce, int price, uint quantity,
            ICollection<MarketEvent> events)
        {
            if (!_book.TryResolveSubmission(orderId, side, type, timeInForce, price, quantity,
                    out var limit, out var restingPrice))
            {
                var rejected = _book.Submit(orderId, side, type, timeInForce, price, quantity,
                    events);
                return new OrderEntryResult(rejected,
                    new ReservationDecision(RiskRejectReason.InvalidOrder),
                    RiskDecision.Accepted);
            }

            var unitRiskValue = RiskUnitValue(side, type, timeInForce, limit, restingPrice);
            var policy = RiskDecision.Accepted;
            string participantId = null;
            var reservedNotional = CheckedNotional(unitRiskValue, quantity);

            if (_policy is not null)
            {
                if (!_participants.TryGetValue(accountId, out participantId))
                {
                    policy = RiskDecision.Reject(RiskRejectReason.NotEntitled);
                }
                else
                {
                    var request = new OrderRequest(participantId, InstrumentId, side, price,
                        quantity, type);
                    policy = _policy.Check(request, reservedNotional);
                }

                if (!policy.IsAccepted)
                {
                    return new OrderEntryResult(
                        new SubmitResult(orderId, 0, 0, Rejected: true),
                        ReservationDecision.Pass, policy);
                }
            }

            var selfTrade = new SameAccountPredicate(_risk, accountId);
            if (_book.WouldExecute(side, limit, quantity, ref selfTrade))
            {
                if (_policy is not null)
                    _policy.OnOrderClosed(participantId, reservedNotional);

                return new OrderEntryResult(
                    new SubmitResult(orderId, 0, 0, Rejected: true),
                    new ReservationDecision(RiskRejectReason.SelfTrade), policy);
            }

            var decision = _risk.Reserve(accountId, orderId, InstrumentId, side, quantity,
                unitRiskValue);

            if (!decision.Accepted)
            {
                if (_policy is not null)
                    _policy.OnOrderClosed(participantId, reservedNotional);

                return new OrderEntryResult(
                    new SubmitResult(orderId, 0, 0, Rejected: true), decision,
                    policy);
            }

            _events.Clear();
            var matching = _book.SubmitValidated(orderId, side, type, timeInForce, quantity,
                limit, restingPrice, _events);

            if (matching.Rejected)
            {
                Release(orderId, quantity);
            }
            else
            {
                ApplyTrades(_events);

                var unfilled = quantity - matching.FilledQuantity;
                if (matching.RestingQuantity == 0 && unfilled != 0)
                    Release(orderId, unfilled);
                else if (matching.RestingQuantity != 0)
                    Require(_risk.TryGetReservation(orderId, out var reservation) &&
                        reservation.RemainingQuantity == matching.RestingQuantity);
            }

            CopyEvents(_events, events);
            return new OrderEntryResult(matching, decision, policy);
        }

        public OrderActionResult Cancel(ulong accountId, ulong orderId,
            ICollection<MarketEvent> events)
        {
            var ownership = CheckOwnership(accountId, orderId, out var reservation);
            if (ownership != OrderActionResult.Applied)
                return ownership;

            _events.Clear();
            Require(_book.Cancel(orderId, _events));
            Release(orderId, reservation.RemainingQuantity);
            CopyEvents(_events, events);
            return OrderActionResult.Applied;
        }

        public OrderActionResult Reduce(ulong accountId, ulong orderId, uint newQuantity,
            ICollection<MarketEvent> events)
        {
            var ownership = CheckOwnership(accountId, orderId, out var reservation);
            if (ownership != OrderActionResult.Applied)
                return ownership;
            if (newQuantity > reservation.RemainingQuantity)
                return OrderActionResult.InvalidQuantity;

            _events.Clear();
            Require(_book.Reduce(orderId, newQuantity, _events));

            var released = reservation.RemainingQuantity - newQuantity;
            if (released != 0)
                Release(orderId, released);

            CopyEvents(_events, events);
            return OrderActionResult.Applied;
        }

        public bool SetAccountKill(ulong accountId, bool killed, bool cancelResting,
            ICollection<MarketEvent> events)
        {
            ParticipantRiskState participant = null;
            if (_policy is not null &&
                (!_participants.TryGetValue(accountId, out var participantId) ||
                 (participant = _policy.StateOf(participantId)) is null))
                return false;
            if (!_risk.SetAccountKill(accountId, killed))
                return false;

            if (participant is not null)
            {
                if (killed)
                    participant.Kill();
                else
                    participant.Revive();
            }

            if (killed && cancelResting)
                CancelOrders(_risk.ActiveOrderIds(accountId, InstrumentId), events);
            return true;
        }

        public void SetGlobalKill(bool killed, bool cancelResting,
            ICollection<MarketEvent> events)
        {
            _risk.SetGlobalKill(killed);
            if (_policy is not null)
            {
                if (killed)
                    _policy.EngageGlobalKill();
                else
                    _policy.ReleaseGlobalKill();
            }

            if (killed && cancelResting)
                CancelOrders(_risk.ActiveOrderIds(InstrumentId), events);
        }

        public Order Find(ulong orderId) => _book.Find(orderId);

        public bool TryGetBest(Side side, out int price, out ulong quantity)
            => _book.TryGetBest(side, out price, out quantity);

        public ulong QuantityAt(Side side, int price) => _book.QuantityAt(side, price);

        public int CopyDepth(Side side, Span<PriceLevel> destination)
            => _book.CopyDepth(side, destination);

        public IEnumerable<Order> OrdersInPriority(Side side) => _book.OrdersInPriority(side);

        private OrderActionResult CheckOwnership(ulong accountId, ulong orderId,
            out RiskReservation reservation)
        {
            if (!_risk.TryGetReservation(orderId, out reservation) ||
                reservation.InstrumentId != InstrumentId)
                return OrderActionResult.UnknownOrder;
            return reservation.AccountId == accountId
                ? OrderActionResult.Applied
                : OrderActionResult.NotOwner;
        }

        private void ApplyTrades(List<MarketEvent> marketEvents)
        {
            foreach (var marketEvent in marketEvents)
            {
                if (marketEvent.Type != MarketEventType.Traded)
                    continue;

                ApplyFill(marketEvent.OrderId, marketEvent.Quantity);
                ApplyFill(marketEvent.CounterpartyOrderId, marketEvent.Quantity);
            }
        }

        private void CancelOrders(ulong[] orderIds, ICollection<MarketEvent> events)
        {
            foreach (var orderId in orderIds)
            {
                _events.Clear();
                Require(_book.Cancel(orderId, _events));
                Require(_risk.TryGetReservation(orderId, out var reservation));
                Release(orderId, reservation.RemainingQuantity);
                CopyEvents(_events, events);
            }
        }

        private void ApplyFill(ulong orderId, uint quantity)
        {
            Require(_risk.TryGetReservation(orderId, out var reservation));
            Require(_risk.TryApplyFill(orderId, quantity));

            if (_policy is null)
                return;

            Require(_participants.TryGetValue(reservation.AccountId, out var participantId));
            var signedQuantity = reservation.Side == Side.Bid
                ? quantity
                : -(long)quantity;
            _policy.OnFill(participantId, InstrumentId, signedQuantity,
                CheckedNotional(reservation.UnitRiskValue, quantity));
        }

        private void Release(ulong orderId, uint quantity)
        {
            Require(_risk.TryGetReservation(orderId, out var reservation));
            Require(_risk.TryRelease(orderId, quantity));

            if (_policy is null)
                return;

            Require(_participants.TryGetValue(reservation.AccountId, out var participantId));
            _policy.OnOrderClosed(participantId,
                CheckedNotional(reservation.UnitRiskValue, quantity));
        }

        private static void CopyEvents(List<MarketEvent> source,
            ICollection<MarketEvent> destination)
        {
            if (destination is null)
                return;

            foreach (var marketEvent in source)
                destination.Add(marketEvent);
        }

        private static ulong Absolute(int value) => (ulong)Math.Abs((long)value);

        private static long CheckedNotional(ulong unitRiskValue, uint quantity)
        {
            var notional = (UInt128)unitRiskValue * quantity;
            if (notional > long.MaxValue)
                throw new OverflowException("Risk notional exceeds the policy representation.");
            return (long)notional;
        }

        private ulong RiskUnitValue(Side side, OrderType type, TimeInForce timeInForce,
            int limit, int restingPrice)
        {
            if (type == OrderType.Market)
                return Math.Max(Absolute(MinPrice), Absolute(MaxPrice));
            if (type == OrderType.MarketToLimit || timeInForce == TimeInForce.GoodTilCrossing)
                return Absolute(restingPrice);

            var opposite = side == Side.Bid ? Side.Ask : Side.Bid;
            var crosses = _book.TryGetBest(opposite, out var touch, out _) &&
                (side == Side.Bid ? touch <= limit : touch >= limit);

            if (!crosses)
                return Absolute(restingPrice);

            return side == Side.Bid
                ? Math.Max(Absolute(MinPrice), Absolute(limit))
                : Math.Max(Absolute(limit), Absolute(MaxPrice));
        }

        private static void Require(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException("Matching and risk state diverged.");
        }

        private readonly struct SameAccountPredicate : IExecutableOrderPredicate
        {
            private readonly PreTradeRiskEngine _risk;
            private readonly ulong _accountId;

            public SameAccountPredicate(PreTradeRiskEngine risk, ulong accountId)
            {
                _risk = risk;
                _accountId = accountId;
            }

            public bool Matches(Order order)
                => _risk.TryGetReservation(order.Id, out var reservation) &&
                    reservation.AccountId == _accountId;
        }
    }
}
