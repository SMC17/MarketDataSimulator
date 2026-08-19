using MarketData.Common.Books;
using System;

namespace MarketData.Common.Matching
{
    public enum OrderType : byte
    {
        Limit = 0,

        /// <summary>Takes liquidity at any price; never rests.</summary>
        Market = 1,

        /// <summary>
        /// Trades only at the current opposite touch, then rests any remainder at that price.
        /// Rejected when the opposite book is empty.
        /// </summary>
        MarketToLimit = 2,
    }

    public enum TimeInForce : byte
    {
        /// <summary>Rest any unfilled remainder on the book.</summary>
        GoodTilCancel = 0,

        /// <summary>Fill what is available immediately, cancel the rest.</summary>
        ImmediateOrCancel = 1,

        /// <summary>Fill entirely and immediately, or do nothing at all.</summary>
        FillOrKill = 2,

        /// <summary>Rest without taking liquidity; reject if the order would cross.</summary>
        GoodTilCrossing = 3,
    }

    /// <summary>
    /// A single resting order, and simultaneously a node in its price level's FIFO queue.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The list pointers live on the order itself - an intrusive list - rather than in separate
    /// node objects. That is what makes cancellation O(1): a cancel arrives with an order id, the
    /// id map yields this object directly, and unlinking it needs only its two neighbours. A
    /// non-intrusive queue would force a scan of the level to find the order first, and since real
    /// order flow is overwhelmingly cancels, that scan would be the whole cost of the book.
    /// </para>
    /// <para>
    /// A class rather than a struct, deliberately: the identity of an order is the point. The id
    /// map, its price level and the matching loop all refer to the same object, and copying it
    /// would break the linkage that makes the structure work.
    /// </para>
    /// </remarks>
    public sealed class Order
    {
        public ulong Id;
        public Side Side;
        public int Price;

        /// <summary>Size when the order was accepted.</summary>
        public uint Quantity;

        /// <summary>Unfilled size. Zero means fully executed.</summary>
        public uint Remaining;

        internal Order Previous;
        internal Order Next;
        internal OrderLevel Level;

        public bool IsResting => Level is not null;
        public uint Filled => Quantity - Remaining;

        public override string ToString()
            => $"#{Id} {Side} {Remaining}/{Quantity} @ {Price}";
    }

    /// <summary>
    /// One price point: every order resting at that price, in arrival order.
    /// </summary>
    /// <remarks>
    /// Head is the oldest order and therefore next to fill; tail is where arrivals join. Keeping
    /// both ends means adding and matching are O(1) and never walk the queue.
    /// <see cref="TotalQuantity"/> is maintained incrementally so the aggregated depth feed can be
    /// produced without summing the queue.
    /// </remarks>
    public sealed class OrderLevel
    {
        public int Price;
        public Order Head;
        public Order Tail;

        /// <summary>Sum of <see cref="Order.Remaining"/> across the queue, maintained incrementally.</summary>
        public ulong TotalQuantity;

        public int OrderCount;

        public bool IsEmpty => OrderCount == 0;

        public void Reset()
        {
            Head = null;
            Tail = null;
            TotalQuantity = 0;
            OrderCount = 0;
        }
    }
}
