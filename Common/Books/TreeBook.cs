using System;
using System.Collections.Generic;

namespace MarketData.Common.Books
{
    /// <summary>
    /// The reference design: a balanced binary search tree over prices for ordering, alongside a
    /// hash map from price to quantity for point lookups.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is how a general-purpose book is usually built, and it is the shape to reach for when
    /// the price space is unbounded or sparse: no band to size up front, no depth cap needed for
    /// correctness, and memory proportional to live levels rather than to the price range.
    /// Every operation is O(log d) - including both endpoints, since the tree yields the touch and
    /// the tail by walking to its leftmost and rightmost nodes.
    /// </para>
    /// <para>
    /// The pairing matters. A tree alone would make "what is the quantity at this price" a search;
    /// a hash map alone would make "what is the best price" a full scan. Each structure covers the
    /// other's weak operation, at the cost of keeping two of them consistent.
    /// </para>
    /// <para>
    /// What it cannot fix is locality. Every node is a separate heap object, so a descent is a
    /// chain of dependent loads the prefetcher cannot run ahead of, and reading the top ten levels
    /// chases pointers instead of streaming one contiguous run of memory. Against ten levels
    /// sitting in a single cache line, an asymptotically better algorithm still loses.
    /// </para>
    /// </remarks>
    public sealed class TreeBook : IOrderBook
    {
        public int Depth { get; }

        public TreeBook(int depth)
        {
            if (depth <= 0)
                throw new ArgumentOutOfRangeException(nameof(depth), depth, "Depth must be positive.");

            Depth = depth;

            _prices = new SortedSet<int>[2];
            _prices[(int)Side.Bid] = new SortedSet<int>(TouchFirstComparer.Bid);
            _prices[(int)Side.Ask] = new SortedSet<int>(TouchFirstComparer.Ask);

            _quantities = new Dictionary<int, uint>[2];
            _quantities[(int)Side.Bid] = new Dictionary<int, uint>(depth * 2);
            _quantities[(int)Side.Ask] = new Dictionary<int, uint>(depth * 2);
        }

        public int Count(Side side) => _prices[(int)side].Count;

        public bool TryGetBest(Side side, out PriceLevel level)
        {
            var prices = _prices[(int)side];

            if (prices.Count == 0)
            {
                level = default;
                return false;
            }

            // Ordered touch-first, so the set's minimum under that comparer is the touch.
            var price = prices.Min;
            level = new PriceLevel(price, _quantities[(int)side][price]);
            return true;
        }

        public bool TryGetQuantity(Side side, int price, out uint quantity)
            => _quantities[(int)side].TryGetValue(price, out quantity);

        public bool Upsert(Side side, int price, uint quantity)
        {
            if (quantity == 0)
                return Remove(side, price);

            var index = (int)side;
            var quantities = _quantities[index];

            if (quantities.TryGetValue(price, out var existing))
            {
                if (existing == quantity)
                    return false;

                quantities[price] = quantity;
                return true;
            }

            var prices = _prices[index];

            if (prices.Count == Depth)
            {
                var tail = prices.Max;

                if (!SideOrder.IsBetter(side, price, tail))
                    return false;

                prices.Remove(tail);
                quantities.Remove(tail);
            }

            prices.Add(price);
            quantities.Add(price, quantity);
            return true;
        }

        public bool Remove(Side side, int price)
        {
            var index = (int)side;

            if (!_prices[index].Remove(price))
                return false;

            _quantities[index].Remove(price);
            return true;
        }

        public int CopyTo(Side side, Span<PriceLevel> destination)
        {
            var index = (int)side;
            var quantities = _quantities[index];
            var written = 0;

            foreach (var price in _prices[index])
            {
                if (written == destination.Length)
                    break;

                destination[written++] = new PriceLevel(price, quantities[price]);
            }

            return written;
        }

        public void Clear()
        {
            for (var side = 0; side < 2; side++)
            {
                _prices[side].Clear();
                _quantities[side].Clear();
            }
        }

        private sealed class TouchFirstComparer : IComparer<int>
        {
            public static readonly TouchFirstComparer Bid = new TouchFirstComparer(Side.Bid);
            public static readonly TouchFirstComparer Ask = new TouchFirstComparer(Side.Ask);

            private TouchFirstComparer(Side side) => _side = side;

            public int Compare(int x, int y) => SideOrder.Compare(_side, x, y);

            private readonly Side _side;
        }

        private readonly SortedSet<int>[] _prices;
        private readonly Dictionary<int, uint>[] _quantities;
    }
}
