using System;
using System.Collections.Generic;

namespace MarketData.Common.Books
{
    /// <summary>
    /// One side-aggregated limit order book, capped at a fixed display depth.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three implementations sit behind this interface - <see cref="SortedArrayBook"/>,
    /// <see cref="LadderBook"/> and <see cref="TreeBook"/> - with deliberately different
    /// complexity profiles. They are held to identical observable behaviour by differential
    /// tests, so the choice between them is purely a performance question and can be made with
    /// measurements rather than argument.
    /// </para>
    /// <para>
    /// Depth is a hard cap. Applying a level worse than the current worst level on a full side is
    /// a no-op; applying one better evicts the worst. This mirrors a depth-limited exchange feed,
    /// where the book a subscriber sees is a truncated view of the real one.
    /// </para>
    /// </remarks>
    public interface IOrderBook
    {
        /// <summary>Maximum number of levels retained per side.</summary>
        int Depth { get; }

        /// <summary>Levels currently present on <paramref name="side"/>.</summary>
        int Count(Side side);

        /// <summary>The touch: best bid (highest) or best ask (lowest).</summary>
        bool TryGetBest(Side side, out PriceLevel level);

        /// <summary>
        /// Inserts a level, or replaces the quantity of an existing one at the same price.
        /// </summary>
        /// <returns>
        /// <c>true</c> if the book changed. A rejected level (worse than the worst on a full
        /// side) returns <c>false</c>.
        /// </returns>
        bool Upsert(Side side, int price, uint quantity);

        /// <summary>Removes the level at <paramref name="price"/>.</summary>
        /// <returns><c>true</c> if a level was present and removed.</returns>
        bool Remove(Side side, int price);

        /// <summary>
        /// Copies up to <paramref name="destination"/>.Length levels, touch first, into the span.
        /// </summary>
        /// <returns>The number of levels written.</returns>
        int CopyTo(Side side, Span<PriceLevel> destination);

        void Clear();
    }

    public static class OrderBookExtensions
    {
        /// <summary>
        /// Best ask minus best bid. Null when either side is empty. A negative spread means the
        /// book is crossed, which is the invariant every book implementation must never violate.
        /// </summary>
        public static int? Spread(this IOrderBook book)
        {
            if (!book.TryGetBest(Side.Bid, out var bid) || !book.TryGetBest(Side.Ask, out var ask))
                return null;

            return ask.Price - bid.Price;
        }

        /// <summary>Materialises a side, touch first. Allocates - for tests and diagnostics only.</summary>
        public static List<PriceLevel> ToList(this IOrderBook book, Side side)
        {
            var buffer = new PriceLevel[book.Depth];
            var count = book.CopyTo(side, buffer);
            var levels = new List<PriceLevel>(count);

            for (var i = 0; i < count; i++)
                levels.Add(buffer[i]);

            return levels;
        }
    }
}
