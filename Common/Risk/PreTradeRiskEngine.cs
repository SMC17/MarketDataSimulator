using System;
using System.Collections.Generic;
using System.Linq;
using MarketData.Common.Books;

namespace MarketData.Common.Risk
{
    /// <summary>Account-wide order/open limits plus a per-instrument position limit.</summary>
    public readonly record struct RiskLimits(
        uint MaxOrderQuantity,
        ulong MaxOrderNotional,
        ulong MaxOpenQuantity,
        ulong MaxOpenNotional,
        long MaxAbsolutePosition,
        int MaxActiveOrders)
    {
        public static RiskLimits Unbounded { get; } = new(
            uint.MaxValue, ulong.MaxValue, ulong.MaxValue, ulong.MaxValue,
            long.MaxValue, int.MaxValue);

        internal void Validate()
        {
            if (MaxOrderQuantity == 0 || MaxOrderNotional == 0 || MaxOpenQuantity == 0 ||
                MaxOpenNotional == 0 || MaxAbsolutePosition <= 0 || MaxActiveOrders <= 0)
                throw new ArgumentOutOfRangeException(nameof(RiskLimits));
        }
    }

    public readonly record struct ReservationDecision(
        RiskRejectReason Reason,
        ulong OrderNotional = 0,
        long ProjectedPosition = 0)
    {
        public static ReservationDecision Pass { get; } =
            new(RiskRejectReason.None);

        public bool Accepted => Reason == RiskRejectReason.None;
    }

    public readonly record struct RiskReservation(
        ulong AccountId,
        ulong OrderId,
        int InstrumentId,
        Side Side,
        uint RemainingQuantity,
        ulong UnitRiskValue);

    public readonly record struct RiskAccountSnapshot(
        ulong AccountId,
        int InstrumentId,
        bool Killed,
        int ActiveOrders,
        ulong OpenQuantity,
        ulong OpenNotional,
        long Position,
        ulong OpenBidQuantity,
        ulong OpenAskQuantity);

    /// <summary>Single-writer pre-trade limits with worst-case open-order reservations.</summary>
    public sealed class PreTradeRiskEngine
    {
        private readonly Dictionary<ulong, AccountState> _accounts = new();
        private readonly Dictionary<ulong, ReservationState> _orders = new();

        public bool GlobalKilled { get; private set; }
        public int ActiveOrders => _orders.Count;

        public void ConfigureAccount(ulong accountId, RiskLimits limits)
        {
            if (accountId == 0)
                throw new ArgumentOutOfRangeException(nameof(accountId));

            limits.Validate();

            if (!_accounts.TryGetValue(accountId, out var account))
            {
                _accounts.Add(accountId, new AccountState(limits));
                return;
            }

            if (!Fits(accountId, account, limits))
                throw new InvalidOperationException("The new limits are below live exposure.");

            account.Limits = limits;
        }

        public void SetGlobalKill(bool killed) => GlobalKilled = killed;

        public bool SetAccountKill(ulong accountId, bool killed)
        {
            if (!_accounts.TryGetValue(accountId, out var account))
                return false;

            account.Killed = killed;
            return true;
        }

        public ReservationDecision SetPosition(ulong accountId, int instrumentId, long position)
        {
            if (instrumentId <= 0)
                return new ReservationDecision(RiskRejectReason.InvalidOrder);
            if (!_accounts.TryGetValue(accountId, out var account))
                return new ReservationDecision(RiskRejectReason.UnknownAccount);

            account.Instruments.TryGetValue(instrumentId, out var exposure);
            var bids = exposure?.OpenBidQuantity ?? 0;
            var asks = exposure?.OpenAskQuantity ?? 0;
            var limit = (Int128)account.Limits.MaxAbsolutePosition;

            if ((Int128)position + bids > limit || (Int128)position - asks < -limit)
                return new ReservationDecision(RiskRejectReason.PositionLimitExceeded);

            if (exposure is null && position == 0)
                return new ReservationDecision(RiskRejectReason.None, ProjectedPosition: position);

            exposure ??= AddExposure(account, instrumentId);
            exposure.Position = position;
            return new ReservationDecision(RiskRejectReason.None, ProjectedPosition: position);
        }

        /// <param name="unitRiskValue">Absolute integer value per unit in the caller's price scale.</param>
        public ReservationDecision Reserve(ulong accountId, ulong orderId, int instrumentId, Side side,
            uint quantity, ulong unitRiskValue)
        {
            if (accountId == 0 || orderId == 0 || instrumentId <= 0 || quantity == 0 ||
                (byte)side > (byte)Side.Ask)
                return new ReservationDecision(RiskRejectReason.InvalidOrder);
            if (!_accounts.TryGetValue(accountId, out var account))
                return new ReservationDecision(RiskRejectReason.UnknownAccount);
            if (GlobalKilled)
                return new ReservationDecision(RiskRejectReason.KillSwitchEngaged);
            if (account.Killed)
                return new ReservationDecision(RiskRejectReason.KillSwitchEngaged);
            if (_orders.ContainsKey(orderId))
                return new ReservationDecision(RiskRejectReason.DuplicateOrderId);

            var limits = account.Limits;
            if (quantity > limits.MaxOrderQuantity)
                return new ReservationDecision(RiskRejectReason.OrderQuantityTooLarge);
            if (!TryMultiply(unitRiskValue, quantity, out var notional))
                return new ReservationDecision(RiskRejectReason.ArithmeticOverflow);
            if (notional > limits.MaxOrderNotional)
                return new ReservationDecision(RiskRejectReason.OrderNotionalTooLarge, notional);
            if (quantity > limits.MaxOpenQuantity - Math.Min(account.OpenQuantity,
                    limits.MaxOpenQuantity))
                return new ReservationDecision(RiskRejectReason.OpenQuantityExceeded, notional);
            if (notional > limits.MaxOpenNotional - Math.Min(account.OpenNotional,
                    limits.MaxOpenNotional))
                return new ReservationDecision(RiskRejectReason.OpenNotionalExceeded, notional);
            if (account.ActiveOrders >= limits.MaxActiveOrders)
                return new ReservationDecision(RiskRejectReason.ActiveOrderLimitExceeded, notional);

            account.Instruments.TryGetValue(instrumentId, out var exposure);
            var position = exposure?.Position ?? 0;
            var openSameSide = side == Side.Bid
                ? exposure?.OpenBidQuantity ?? 0
                : exposure?.OpenAskQuantity ?? 0;
            var projected = side == Side.Bid
                ? (Int128)position + openSameSide + quantity
                : (Int128)position - openSameSide - quantity;
            var positionLimit = (Int128)limits.MaxAbsolutePosition;

            if (projected > positionLimit || projected < -positionLimit)
                return new ReservationDecision(RiskRejectReason.PositionLimitExceeded, notional);

            exposure ??= AddExposure(account, instrumentId);
            if (side == Side.Bid)
                exposure.OpenBidQuantity += quantity;
            else
                exposure.OpenAskQuantity += quantity;

            account.OpenQuantity += quantity;
            account.OpenNotional += notional;
            account.ActiveOrders++;
            _orders.Add(orderId, new ReservationState(accountId, orderId, instrumentId, side,
                quantity, unitRiskValue));

            return new ReservationDecision(RiskRejectReason.None, notional, (long)projected);
        }

        public bool TryApplyFill(ulong orderId, uint quantity)
        {
            if (quantity == 0 || !_orders.TryGetValue(orderId, out var reservation) ||
                quantity > reservation.RemainingQuantity)
                return false;

            var account = _accounts[reservation.AccountId];
            var exposure = account.Instruments[reservation.InstrumentId];
            var next = reservation.Side == Side.Bid
                ? (Int128)exposure.Position + quantity
                : (Int128)exposure.Position - quantity;

            if (next > long.MaxValue || next < long.MinValue)
                return false;

            ReleaseCore(ref reservation, account, exposure, quantity);
            exposure.Position = (long)next;
            return true;
        }

        public bool TryRelease(ulong orderId, uint quantity)
        {
            if (quantity == 0 || !_orders.TryGetValue(orderId, out var reservation) ||
                quantity > reservation.RemainingQuantity)
                return false;

            var account = _accounts[reservation.AccountId];
            var exposure = account.Instruments[reservation.InstrumentId];
            ReleaseCore(ref reservation, account, exposure, quantity);
            return true;
        }

        /// <summary>
        /// Releases every outstanding reservation.
        /// </summary>
        /// <remarks>
        /// For resetting a book wholesale. Releasing each order individually rather than clearing
        /// the map: exposure is accumulated per account and per instrument, and dropping the order
        /// map without unwinding it would leave that exposure charged against accounts forever,
        /// which is a silent and permanent loss of credit capacity.
        /// </remarks>
        public void ReleaseEverything()
        {
            foreach (var orderId in _orders.Keys.ToArray())
                TryReleaseAll(orderId);
        }

        public bool TryReleaseAll(ulong orderId)
            => _orders.TryGetValue(orderId, out var reservation) &&
                TryRelease(orderId, reservation.RemainingQuantity);

        public bool TryGetReservation(ulong orderId, out RiskReservation reservation)
        {
            if (!_orders.TryGetValue(orderId, out var state))
            {
                reservation = default;
                return false;
            }

            reservation = state.Snapshot();
            return true;
        }

        public bool TryGetAccount(ulong accountId, int instrumentId,
            out RiskAccountSnapshot snapshot)
        {
            if (!_accounts.TryGetValue(accountId, out var account))
            {
                snapshot = default;
                return false;
            }

            account.Instruments.TryGetValue(instrumentId, out var exposure);
            snapshot = new RiskAccountSnapshot(accountId, instrumentId, account.Killed,
                account.ActiveOrders, account.OpenQuantity, account.OpenNotional,
                exposure?.Position ?? 0, exposure?.OpenBidQuantity ?? 0,
                exposure?.OpenAskQuantity ?? 0);
            return true;
        }

        public bool TryGetLimits(ulong accountId, out RiskLimits limits)
        {
            if (!_accounts.TryGetValue(accountId, out var account))
            {
                limits = default;
                return false;
            }

            limits = account.Limits;
            return true;
        }

        public ulong[] ActiveOrderIds(ulong accountId, int instrumentId)
        {
            var ids = new List<ulong>();

            foreach (var order in _orders.Values)
                if (order.AccountId == accountId && order.InstrumentId == instrumentId)
                    ids.Add(order.OrderId);

            ids.Sort();
            return ids.ToArray();
        }

        public ulong[] ActiveOrderIds(int instrumentId)
        {
            var ids = new List<ulong>();

            foreach (var order in _orders.Values)
                if (order.InstrumentId == instrumentId)
                    ids.Add(order.OrderId);

            ids.Sort();
            return ids.ToArray();
        }

        public bool Validate(out string error)
        {
            foreach (var pair in _orders)
            {
                var order = pair.Value;
                if (pair.Key != order.OrderId || order.RemainingQuantity == 0 ||
                    !_accounts.TryGetValue(order.AccountId, out var account) ||
                    !account.Instruments.ContainsKey(order.InstrumentId))
                {
                    error = $"invalid reservation {pair.Key}";
                    return false;
                }
            }

            foreach (var accountPair in _accounts)
            {
                var accountId = accountPair.Key;
                var account = accountPair.Value;
                var active = 0;
                ulong quantity = 0;
                ulong notional = 0;

                foreach (var order in _orders.Values)
                {
                    if (order.AccountId != accountId)
                        continue;

                    active++;
                    if (!TryAdd(quantity, order.RemainingQuantity, out quantity) ||
                        !TryMultiply(order.UnitRiskValue, order.RemainingQuantity,
                            out var orderNotional) || !TryAdd(notional, orderNotional, out notional))
                    {
                        error = $"overflow in account {accountId}";
                        return false;
                    }
                }

                if (active != account.ActiveOrders || quantity != account.OpenQuantity ||
                    notional != account.OpenNotional || !Fits(accountId, account, account.Limits))
                {
                    error = $"account aggregate mismatch {accountId}";
                    return false;
                }

                foreach (var exposurePair in account.Instruments)
                {
                    ulong bids = 0;
                    ulong asks = 0;

                    foreach (var order in _orders.Values)
                    {
                        if (order.AccountId != accountId ||
                            order.InstrumentId != exposurePair.Key)
                            continue;

                        if (order.Side == Side.Bid)
                            bids += order.RemainingQuantity;
                        else
                            asks += order.RemainingQuantity;
                    }

                    if (bids != exposurePair.Value.OpenBidQuantity ||
                        asks != exposurePair.Value.OpenAskQuantity)
                    {
                        error = $"instrument aggregate mismatch {accountId}/{exposurePair.Key}";
                        return false;
                    }
                }
            }

            error = null;
            return true;
        }

        private bool Fits(ulong accountId, AccountState account, RiskLimits limits)
        {
            if (account.ActiveOrders > limits.MaxActiveOrders ||
                account.OpenQuantity > limits.MaxOpenQuantity ||
                account.OpenNotional > limits.MaxOpenNotional)
                return false;

            foreach (var order in _orders.Values)
            {
                if (order.AccountId != accountId)
                    continue;
                if (order.RemainingQuantity > limits.MaxOrderQuantity ||
                    !TryMultiply(order.UnitRiskValue, order.RemainingQuantity, out var notional) ||
                    notional > limits.MaxOrderNotional)
                    return false;
            }

            var positionLimit = (Int128)limits.MaxAbsolutePosition;
            foreach (var exposure in account.Instruments.Values)
            {
                if ((Int128)exposure.Position + exposure.OpenBidQuantity > positionLimit ||
                    (Int128)exposure.Position - exposure.OpenAskQuantity < -positionLimit)
                    return false;
            }

            return true;
        }

        private void ReleaseCore(ref ReservationState reservation, AccountState account,
            InstrumentState exposure, uint quantity)
        {
            TryMultiply(reservation.UnitRiskValue, quantity, out var notional);

            if (reservation.Side == Side.Bid)
                exposure.OpenBidQuantity -= quantity;
            else
                exposure.OpenAskQuantity -= quantity;

            account.OpenQuantity -= quantity;
            account.OpenNotional -= notional;
            reservation.RemainingQuantity -= quantity;

            if (reservation.RemainingQuantity != 0)
            {
                _orders[reservation.OrderId] = reservation;
                return;
            }

            _orders.Remove(reservation.OrderId);
            account.ActiveOrders--;
        }

        private static InstrumentState AddExposure(AccountState account, int instrumentId)
        {
            var exposure = new InstrumentState();
            account.Instruments.Add(instrumentId, exposure);
            return exposure;
        }

        private static bool TryMultiply(ulong value, uint quantity, out ulong result)
        {
            if (value != 0 && quantity > ulong.MaxValue / value)
            {
                result = 0;
                return false;
            }

            result = value * quantity;
            return true;
        }

        private static bool TryAdd(ulong left, ulong right, out ulong result)
        {
            if (left > ulong.MaxValue - right)
            {
                result = 0;
                return false;
            }

            result = left + right;
            return true;
        }

        private sealed class AccountState
        {
            public AccountState(RiskLimits limits) => Limits = limits;

            public RiskLimits Limits;
            public bool Killed;
            public int ActiveOrders;
            public ulong OpenQuantity;
            public ulong OpenNotional;
            public readonly Dictionary<int, InstrumentState> Instruments = new();
        }

        private sealed class InstrumentState
        {
            public long Position;
            public ulong OpenBidQuantity;
            public ulong OpenAskQuantity;
        }

        private struct ReservationState
        {
            public ReservationState(ulong accountId, ulong orderId, int instrumentId, Side side,
                uint remainingQuantity, ulong unitRiskValue)
            {
                AccountId = accountId;
                OrderId = orderId;
                InstrumentId = instrumentId;
                Side = side;
                RemainingQuantity = remainingQuantity;
                UnitRiskValue = unitRiskValue;
            }

            public ulong AccountId { get; }
            public ulong OrderId { get; }
            public int InstrumentId { get; }
            public Side Side { get; }
            public uint RemainingQuantity { get; set; }
            public ulong UnitRiskValue { get; }

            public RiskReservation Snapshot()
                => new(AccountId, OrderId, InstrumentId, Side, RemainingQuantity, UnitRiskValue);
        }
    }
}
