using MarketData.Common.Books;
using System;

namespace MarketData.Common.Matching
{
    public enum MarketEventType : byte
    {
        /// <summary>An order joined the book and is now resting.</summary>
        Added = 1,

        /// <summary>A resting order was removed before completion.</summary>
        Cancelled = 2,

        /// <summary>A resting order's size was reduced in place, keeping its queue position.</summary>
        Reduced = 3,

        /// <summary>Two orders traded. Emitted once per fill, from the resting order's perspective.</summary>
        Traded = 4,

        /// <summary>An order was rejected outright and never entered the book.</summary>
        Rejected = 5,
    }

    /// <summary>
    /// Everything the matching engine did, in the order it did it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the order-by-order feed - the same shape of data a real exchange publishes on an
    /// ITCH-style channel - and the aggregated depth feed is derived from it rather than being a
    /// separate source of truth. That direction matters: a subscriber replaying these events must
    /// arrive at exactly the engine's book, and there is a test that says so.
    /// </para>
    /// <para>
    /// A readonly struct, so a burst of events costs no allocation and no collector pressure on
    /// the matching path.
    /// </para>
    /// </remarks>
    public readonly record struct MarketEvent(
        MarketEventType Type,
        ulong OrderId,
        Side Side,
        int Price,
        uint Quantity,
        ulong CounterpartyOrderId)
    {
        public static MarketEvent Added(Order order)
            => new MarketEvent(MarketEventType.Added, order.Id, order.Side, order.Price, order.Remaining, 0);

        public static MarketEvent Cancelled(Order order)
            => new MarketEvent(MarketEventType.Cancelled, order.Id, order.Side, order.Price, order.Remaining, 0);

        public static MarketEvent Reduced(Order order)
            => new MarketEvent(MarketEventType.Reduced, order.Id, order.Side, order.Price, order.Remaining, 0);

        /// <param name="resting">The order that was on the book and is being filled.</param>
        /// <param name="aggressorId">The incoming order that took the liquidity.</param>
        /// <param name="quantity">Size of this fill.</param>
        public static MarketEvent Traded(Order resting, ulong aggressorId, uint quantity)
            => new MarketEvent(MarketEventType.Traded, resting.Id, resting.Side, resting.Price, quantity, aggressorId);

        public static MarketEvent Rejected(ulong orderId, Side side, int price, uint quantity)
            => new MarketEvent(MarketEventType.Rejected, orderId, side, price, quantity, 0);

        public override string ToString() => Type switch
        {
            MarketEventType.Traded => $"Traded {Quantity} @ {Price} (resting #{OrderId} vs #{CounterpartyOrderId})",
            _ => $"{Type} #{OrderId} {Side} {Quantity} @ {Price}",
        };
    }

    /// <summary>Outcome of submitting an order.</summary>
    public readonly record struct SubmitResult(
        ulong OrderId,
        uint FilledQuantity,
        uint RestingQuantity,
        bool Rejected)
    {
        public bool FullyFilled => RestingQuantity == 0 && !Rejected && FilledQuantity > 0;
    }
}
