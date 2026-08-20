using System;
using System.Threading;

namespace MarketData.Common.Risk
{
    /// <summary>Static limits for one participant. Immutable; replaced rather than mutated.</summary>
    /// <param name="MaxOrderQuantity">Largest single order, in lots.</param>
    /// <param name="MaxOrderNotional">Largest single order, in price units times quantity.</param>
    /// <param name="MaxNetPosition">Largest absolute net position per instrument.</param>
    /// <param name="CreditLimit">Total notional exposure permitted at once.</param>
    /// <param name="MaxMessagesPerSecond">Sustained message allowance.</param>
    /// <param name="CollarBasisPoints">
    /// How far from the reference price an order may be priced. Zero disables the check.
    /// </param>
    public sealed record ParticipantLimits(
        uint MaxOrderQuantity = 100_000,
        long MaxOrderNotional = 10_000_000_000,
        long MaxNetPosition = 1_000_000,
        long CreditLimit = 100_000_000_000,
        int MaxMessagesPerSecond = 10_000,
        int CollarBasisPoints = 1_000)
    {
        public static ParticipantLimits Default { get; } = new();

        internal void Validate()
        {
            if (MaxOrderQuantity == 0 || MaxOrderNotional <= 0 || MaxNetPosition <= 0 ||
                CreditLimit <= 0 || MaxMessagesPerSecond <= 0 || CollarBasisPoints < 0)
                throw new ArgumentOutOfRangeException(nameof(ParticipantLimits));
        }
    }

    /// <summary>
    /// A participant's live risk state: credit, positions, message rate, and kill switch.
    /// </summary>
    /// <remarks>
    /// Credit mutation, fills, rate state, and limit replacement share one participant-local lock.
    /// That makes check-and-reserve and reconfiguration linearizable without a venue-wide lock.
    /// Kill and telemetry counters remain interlocked.
    /// </remarks>
    public sealed class ParticipantRiskState
    {
        private readonly object _stateGate = new();
        private readonly System.Collections.Generic.Dictionary<int, long> _positions = new();

        private ParticipantLimits _limits;
        private long _reservedCredit;
        private int _killed;
        private long _acceptedOrders;
        private long _rejectedOrders;

        // Token bucket, in whole messages, refilled from elapsed time.
        private long _rateTokens;
        private long _lastRefillTicks;

        public ParticipantRiskState(string participantId, ParticipantLimits limits)
        {
            ParticipantId = participantId ?? throw new ArgumentNullException(nameof(participantId));
            _limits = limits ?? throw new ArgumentNullException(nameof(limits));
            limits.Validate();

            _rateTokens = limits.MaxMessagesPerSecond;
            _lastRefillTicks = Environment.TickCount64;
        }

        public string ParticipantId { get; }
        public ParticipantLimits Limits => Volatile.Read(ref _limits);

        public long ReservedCredit => Interlocked.Read(ref _reservedCredit);
        public long AvailableCredit => Math.Max(0, Limits.CreditLimit - ReservedCredit);
        public bool IsKilled => Volatile.Read(ref _killed) != 0;

        public long RejectedOrders => Interlocked.Read(ref _rejectedOrders);
        public long AcceptedOrders => Interlocked.Read(ref _acceptedOrders);

        public void ReplaceLimits(ParticipantLimits limits)
        {
            ArgumentNullException.ThrowIfNull(limits);
            limits.Validate();

            lock (_stateGate)
            {
                if (ReservedCredit > limits.CreditLimit)
                    throw new InvalidOperationException("The credit limit is below live exposure.");

                foreach (var position in _positions.Values)
                {
                    var magnitude = position < 0 ? -(Int128)position : (Int128)position;
                    if (magnitude > limits.MaxNetPosition)
                        throw new InvalidOperationException("The position limit is below live exposure.");
                }

                Volatile.Write(ref _limits, limits);
                _rateTokens = Math.Min(_rateTokens, limits.MaxMessagesPerSecond);
            }
        }

        /// <summary>Engages the kill switch. Idempotent.</summary>
        /// <returns>True if this call was the one that engaged it.</returns>
        public bool Kill() => Interlocked.Exchange(ref _killed, 1) == 0;

        /// <summary>
        /// Releases the kill switch.
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="Kill"/> and never automatic. A kill switch that can re-arm
        /// itself on a timer is not a kill switch: whatever tripped it is still true until a human
        /// says otherwise.
        /// </remarks>
        public bool Revive() => Interlocked.Exchange(ref _killed, 0) == 1;

        /// <summary>
        /// Reserves credit if it fits, atomically.
        /// </summary>
        /// <returns>False if the reservation would exceed the limit; nothing is reserved then.</returns>
        public bool TryReserveCredit(long notional)
        {
            if (notional <= 0)
                return true;

            lock (_stateGate)
            {
                var limit = _limits.CreditLimit;

                if (_reservedCredit < 0 || _reservedCredit > limit ||
                    notional > limit - _reservedCredit)
                    return false;

                _reservedCredit += notional;
                return true;
            }
        }

        /// <summary>Returns credit when an order is cancelled, filled or expires.</summary>
        public void ReleaseCredit(long notional)
        {
            if (notional <= 0)
                return;

            // Clamped at zero. Releasing more than was reserved is a bug in the caller, but
            // letting reserved credit go negative would hand that participant unlimited capacity,
            // which turns an accounting bug into a risk failure.
            lock (_stateGate)
            {
                _reservedCredit = Math.Max(0, _reservedCredit - notional);
            }
        }

        public long PositionIn(int instrumentId)
        {
            lock (_stateGate)
                return _positions.TryGetValue(instrumentId, out var position) ? position : 0;
        }

        /// <summary>Applies a fill. Positive for a buy, negative for a sell.</summary>
        public void ApplyFill(int instrumentId, long signedQuantity)
        {
            lock (_stateGate)
            {
                _positions.TryGetValue(instrumentId, out var current);
                _positions[instrumentId] = checked(current + signedQuantity);
            }
        }

        internal void ApplyFillAndReleaseCredit(int instrumentId, long signedQuantity,
            long notional)
        {
            lock (_stateGate)
            {
                _positions.TryGetValue(instrumentId, out var current);
                _positions[instrumentId] = checked(current + signedQuantity);
                if (notional > 0)
                    _reservedCredit = Math.Max(0, _reservedCredit - notional);
            }
        }

        /// <summary>Whether a prospective fill would stay inside the net position limit.</summary>
        public bool WouldStayWithinPositionLimit(int instrumentId, long signedQuantity)
        {
            lock (_stateGate)
            {
                _positions.TryGetValue(instrumentId, out var current);
                var proposed = (Int128)current + signedQuantity;
                var magnitude = proposed < 0 ? -proposed : proposed;
                return magnitude <= _limits.MaxNetPosition;
            }
        }

        /// <summary>
        /// Takes one message from the rate allowance.
        /// </summary>
        /// <remarks>
        /// A token bucket rather than a fixed window, because a fixed window lets a participant
        /// send its entire allowance in the last millisecond of one window and again in the first
        /// of the next - twice the intended rate, entirely within the stated limit. The bucket
        /// bounds the burst as well as the average.
        /// </remarks>
        public bool TryConsumeRateToken()
        {
            var now = Environment.TickCount64;

            lock (_stateGate)
            {
                var elapsedMs = now - _lastRefillTicks;

                if (elapsedMs > 0)
                {
                    var refillWide = (Int128)_limits.MaxMessagesPerSecond * elapsedMs / 1000;
                    var refill = refillWide > long.MaxValue ? long.MaxValue : (long)refillWide;

                    if (refill > 0)
                    {
                        var capacity = _limits.MaxMessagesPerSecond;
                        _rateTokens = refill >= capacity - _rateTokens
                            ? capacity
                            : _rateTokens + refill;
                        _lastRefillTicks = now;
                    }
                }

                if (_rateTokens <= 0)
                    return false;

                _rateTokens--;
                return true;
            }
        }

        internal void RecordAccepted() => Interlocked.Increment(ref _acceptedOrders);
        internal void RecordRejected() => Interlocked.Increment(ref _rejectedOrders);
    }
}
