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
    }

    /// <summary>
    /// A participant's live risk state: credit, positions, message rate, and kill switch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every counter here is touched on the order path, so all of it is interlocked rather than
    /// locked. The check-then-act shape matters: credit is <em>reserved</em> before an order is
    /// accepted and released when it is cancelled or filled, because checking a limit and then
    /// acting on it without reserving lets two concurrent orders both pass a check that only one
    /// of them should have.
    /// </para>
    /// <para>
    /// Reservation uses a compare-and-swap loop rather than a plain add-then-check-then-subtract.
    /// The latter transiently exceeds the limit, and anything sampling exposure in that window -
    /// including another thread's check - sees a number that was never permitted.
    /// </para>
    /// </remarks>
    public sealed class ParticipantRiskState
    {
        private readonly object _positionGate = new();
        private readonly System.Collections.Generic.Dictionary<int, long> _positions = new();

        private long _reservedCredit;
        private int _killed;

        // Token bucket, in whole messages, refilled from elapsed time.
        private long _rateTokens;
        private long _lastRefillTicks;

        public ParticipantRiskState(string participantId, ParticipantLimits limits)
        {
            ParticipantId = participantId ?? throw new ArgumentNullException(nameof(participantId));
            Limits = limits ?? throw new ArgumentNullException(nameof(limits));

            _rateTokens = limits.MaxMessagesPerSecond;
            _lastRefillTicks = Environment.TickCount64;
        }

        public string ParticipantId { get; }
        public ParticipantLimits Limits { get; private set; }

        public long ReservedCredit => Interlocked.Read(ref _reservedCredit);
        public long AvailableCredit => Limits.CreditLimit - ReservedCredit;
        public bool IsKilled => Volatile.Read(ref _killed) != 0;

        public long RejectedOrders { get; private set; }
        public long AcceptedOrders { get; private set; }

        public void ReplaceLimits(ParticipantLimits limits)
            => Limits = limits ?? throw new ArgumentNullException(nameof(limits));

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

            while (true)
            {
                var current = Interlocked.Read(ref _reservedCredit);
                var proposed = current + notional;

                if (proposed > Limits.CreditLimit)
                    return false;

                if (Interlocked.CompareExchange(ref _reservedCredit, proposed, current) == current)
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
            while (true)
            {
                var current = Interlocked.Read(ref _reservedCredit);
                var proposed = Math.Max(0, current - notional);

                if (Interlocked.CompareExchange(ref _reservedCredit, proposed, current) == current)
                    return;
            }
        }

        public long PositionIn(int instrumentId)
        {
            lock (_positionGate)
                return _positions.TryGetValue(instrumentId, out var position) ? position : 0;
        }

        /// <summary>Applies a fill. Positive for a buy, negative for a sell.</summary>
        public void ApplyFill(int instrumentId, long signedQuantity)
        {
            lock (_positionGate)
            {
                _positions.TryGetValue(instrumentId, out var current);
                _positions[instrumentId] = current + signedQuantity;
            }
        }

        /// <summary>Whether a prospective fill would stay inside the net position limit.</summary>
        public bool WouldStayWithinPositionLimit(int instrumentId, long signedQuantity)
        {
            lock (_positionGate)
            {
                _positions.TryGetValue(instrumentId, out var current);
                return Math.Abs(current + signedQuantity) <= Limits.MaxNetPosition;
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

            lock (_positionGate)
            {
                var elapsedMs = now - _lastRefillTicks;

                if (elapsedMs > 0)
                {
                    var refill = Limits.MaxMessagesPerSecond * elapsedMs / 1000;

                    if (refill > 0)
                    {
                        _rateTokens = Math.Min(Limits.MaxMessagesPerSecond, _rateTokens + refill);
                        _lastRefillTicks = now;
                    }
                }

                if (_rateTokens <= 0)
                    return false;

                _rateTokens--;
                return true;
            }
        }

        internal void RecordAccepted() => AcceptedOrders++;
        internal void RecordRejected() => RejectedOrders++;
    }
}
