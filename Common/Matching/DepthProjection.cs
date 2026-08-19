using MarketData.Common.Books;
using System.Collections.Generic;

namespace MarketData.Common.Matching
{
    /// <summary>One aggregated price level that changed, and its new total size.</summary>
    public readonly record struct LevelChange(Side Side, int Price, ulong Quantity)
    {
        /// <summary>Zero size means the level has left the book entirely.</summary>
        public bool IsRemoval => Quantity == 0;
    }

    /// <summary>
    /// Projects order-by-order events onto aggregated price levels.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A depth feed is a function of the order book, not an independent stream. Deriving it -
    /// re-reading whichever levels the engine's events touched - keeps one source of truth, so a
    /// subscriber applying the depth feed necessarily agrees with the engine. Maintaining depth
    /// separately alongside the book would create two things that must be kept consistent, and
    /// they would eventually disagree.
    /// </para>
    /// <para>
    /// Several events in one batch commonly touch the same price - a sweep fills three orders at
    /// one level - so each level is emitted once, carrying its final size rather than one update
    /// per fill.
    /// </para>
    /// </remarks>
    public sealed class DepthProjection
    {
        /// <summary>Appends one change per distinct price touched by <paramref name="events"/>.</summary>
        public void Project(LimitOrderBook book, IReadOnlyList<MarketEvent> events, ICollection<LevelChange> changes)
        {
            _seen.Clear();

            for (var i = 0; i < events.Count; i++)
            {
                var marketEvent = events[i];

                if (marketEvent.Type == MarketEventType.Rejected)
                    continue;

                // Side and price together identify a level; packed into one key to avoid
                // allocating a tuple per event on the publish path.
                var key = ((long)marketEvent.Side << 32) | (uint)marketEvent.Price;

                if (!_seen.Add(key))
                    continue;

                changes.Add(new LevelChange(marketEvent.Side, marketEvent.Price,
                    book.QuantityAt(marketEvent.Side, marketEvent.Price)));
            }
        }

        private readonly HashSet<long> _seen = new HashSet<long>();
    }
}
