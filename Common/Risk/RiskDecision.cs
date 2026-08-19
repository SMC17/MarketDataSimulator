using System;

namespace MarketData.Common.Risk
{
    /// <summary>Stable reason codes for policy and execution-risk rejection.</summary>
    public enum RiskRejectReason : byte
    {
        None = 0,

        /// <summary>The participant is not permitted to trade this instrument.</summary>
        NotEntitled,

        /// <summary>A kill switch is engaged, for this participant or globally.</summary>
        KillSwitchEngaged,

        /// <summary>The venue is not in a state that accepts this order.</summary>
        SessionClosed,

        /// <summary>Quantity above the per-order cap.</summary>
        OrderQuantityTooLarge,

        /// <summary>Notional above the per-order cap.</summary>
        OrderNotionalTooLarge,

        /// <summary>Price outside the collar around the reference price.</summary>
        PriceOutsideCollar,

        /// <summary>Price not a multiple of the instrument's tick size.</summary>
        PriceNotOnTick,

        /// <summary>Quantity not a multiple of the lot size.</summary>
        QuantityNotOnLot,

        /// <summary>Would exceed the participant's net position limit.</summary>
        PositionLimitExceeded,

        /// <summary>Would exceed available credit.</summary>
        InsufficientCredit,

        /// <summary>The participant is sending faster than its allowance.</summary>
        RateLimitExceeded,

        /// <summary>Would trade against the participant's own resting order.</summary>
        SelfTrade,

        /// <summary>The request is structurally invalid or its risk value cannot be represented.</summary>
        InvalidOrder,

        /// <summary>The sequencer-local account has no execution-risk configuration.</summary>
        UnknownAccount,

        /// <summary>An active order already owns this venue order id.</summary>
        DuplicateOrderId,

        /// <summary>Aggregate open quantity would exceed its execution limit.</summary>
        OpenQuantityExceeded,

        /// <summary>Aggregate open notional would exceed its execution limit.</summary>
        OpenNotionalExceeded,

        /// <summary>The account already has the maximum permitted active orders.</summary>
        ActiveOrderLimitExceeded,

        /// <summary>A checked risk calculation could not be represented.</summary>
        ArithmeticOverflow,
    }

    /// <summary>The outcome of the pre-trade gate.</summary>
    /// <remarks>
    /// A struct, and deliberately free of any message string. The gate runs on the order path, and
    /// formatting an explanation for an order that was accepted - which is nearly all of them -
    /// would allocate on the hot path to produce text nobody reads. The reason code is enough to
    /// act on; the human-readable form is built later, off the path, by whatever logs it.
    /// </remarks>
    public readonly struct RiskDecision : IEquatable<RiskDecision>
    {
        private RiskDecision(RiskRejectReason reason, long detail)
        {
            Reason = reason;
            Detail = detail;
        }

        public static readonly RiskDecision Accepted = new(RiskRejectReason.None, 0);

        public static RiskDecision Reject(RiskRejectReason reason, long detail = 0)
        {
            if (reason == RiskRejectReason.None)
                throw new ArgumentOutOfRangeException(nameof(reason), "A rejection needs a reason.");

            return new RiskDecision(reason, detail);
        }

        public RiskRejectReason Reason { get; }

        /// <summary>
        /// The limit or value that triggered the rejection, for the log to render later.
        /// </summary>
        public long Detail { get; }

        public bool IsAccepted => Reason == RiskRejectReason.None;

        public bool Equals(RiskDecision other) => Reason == other.Reason && Detail == other.Detail;
        public override bool Equals(object obj) => obj is RiskDecision other && Equals(other);
        public override int GetHashCode() => HashCode.Combine((byte)Reason, Detail);
        public override string ToString() => IsAccepted ? "accepted" : $"rejected: {Reason} ({Detail})";
    }
}
