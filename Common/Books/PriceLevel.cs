using System;

namespace MarketData.Common.Books
{
    /// <summary>Which side of the book a level sits on.</summary>
    public enum Side : byte
    {
        Bid = 0,
        Ask = 1,
    }

    /// <summary>
    /// A single aggregated price level.
    /// </summary>
    /// <remarks>
    /// Prices are integer ticks, never floating point: tick arithmetic has to be exact, and a book
    /// keyed by <c>double</c> will eventually fail to find a level it just inserted. A readonly
    /// record struct keeps levels inline in the book's storage, so walking a side touches one
    /// contiguous run of memory rather than chasing references.
    /// </remarks>
    public readonly record struct PriceLevel(int Price, uint Quantity)
    {
        public bool IsEmpty => Quantity == 0;
    }

    /// <summary>
    /// Orders a side from its touch outwards: best bid is the highest price, best ask the lowest.
    /// Every book implementation presents levels in this order, so callers never branch on side.
    /// </summary>
    public static class SideOrder
    {
        /// <summary>
        /// Negative when <paramref name="a"/> is closer to the touch than <paramref name="b"/>.
        /// </summary>
        public static int Compare(Side side, int a, int b)
            => side == Side.Bid ? b.CompareTo(a) : a.CompareTo(b);

        public static bool IsBetter(Side side, int candidate, int incumbent)
            => side == Side.Bid ? candidate > incumbent : candidate < incumbent;
    }
}
