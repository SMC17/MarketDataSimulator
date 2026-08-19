using System;

namespace MarketData.Common.Books
{
    /// <summary>
    /// Levels held in a flat array per side, kept sorted touch-first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Asymptotically the worst of the three - O(log d) to locate a price, then O(d) to shift the
    /// tail on insert or remove - and in practice the fastest at realistic display depths.
    /// </para>
    /// <para>
    /// The reason is memory, not operation counts. A depth-10 side is 80 bytes: one or two cache
    /// lines, prefetched as a unit, shifted by a <c>memmove</c> the hardware executes at many
    /// bytes per cycle. The tree it is measured against pays a dependent cache miss per level of
    /// descent, and a miss costs more than the entire shift. Complexity classes describe how cost
    /// scales, not what it is at a given size; at d = 10 the constant factor is everything.
    /// </para>
    /// </remarks>
    public sealed class SortedArrayBook : IOrderBook
    {
        public int Depth { get; }

        public SortedArrayBook(int depth)
        {
            if (depth <= 0)
                throw new ArgumentOutOfRangeException(nameof(depth), depth, "Depth must be positive.");

            Depth = depth;
            _sides = new PriceLevel[2][];
            _sides[0] = new PriceLevel[depth];
            _sides[1] = new PriceLevel[depth];
            _counts = new int[2];
        }

        public int Count(Side side) => _counts[(int)side];

        public bool TryGetBest(Side side, out PriceLevel level)
        {
            var index = (int)side;

            if (_counts[index] == 0)
            {
                level = default;
                return false;
            }

            level = _sides[index][0];
            return true;
        }

        public bool TryGetQuantity(Side side, int price, out uint quantity)
        {
            var index = (int)side;
            var position = Locate(side, _sides[index], _counts[index], price, out var exists);

            quantity = exists ? _sides[index][position].Quantity : 0;
            return exists;
        }

        public bool Upsert(Side side, int price, uint quantity)
        {
            if (quantity == 0)
                return Remove(side, price);

            var index = (int)side;
            var levels = _sides[index];
            var count = _counts[index];
            var position = Locate(side, levels, count, price, out var exists);

            if (exists)
            {
                if (levels[position].Quantity == quantity)
                    return false;

                levels[position] = new PriceLevel(price, quantity);
                return true;
            }

            if (count == Depth)
            {
                // Full: a level at or beyond the tail is outside the displayed window entirely.
                if (position == Depth)
                    return false;

                // Otherwise the worst level falls off the end to make room.
                count--;
            }

            Array.Copy(levels, position, levels, position + 1, count - position);
            levels[position] = new PriceLevel(price, quantity);
            _counts[index] = count + 1;
            return true;
        }

        public bool Remove(Side side, int price)
        {
            var index = (int)side;
            var levels = _sides[index];
            var count = _counts[index];
            var position = Locate(side, levels, count, price, out var exists);

            if (!exists)
                return false;

            Array.Copy(levels, position + 1, levels, position, count - position - 1);
            _counts[index] = count - 1;
            levels[count - 1] = default;
            return true;
        }

        public int CopyTo(Side side, Span<PriceLevel> destination)
        {
            var index = (int)side;
            var count = Math.Min(_counts[index], destination.Length);

            _sides[index].AsSpan(0, count).CopyTo(destination);

            return count;
        }

        /// <summary>
        /// Empties both sides in constant time. The arrays keep their contents, which is safe
        /// because nothing ever reads past the live count - and wiping them would make clearing
        /// cost the configured depth rather than nothing.
        /// </summary>
        public void Clear()
        {
            _counts[0] = 0;
            _counts[1] = 0;
        }

        /// <summary>
        /// Binary search for <paramref name="price"/> in touch-first order, returning the index it
        /// occupies or would be inserted at.
        /// </summary>
        private static int Locate(Side side, PriceLevel[] levels, int count, int price, out bool exists)
        {
            var low = 0;
            var high = count - 1;

            while (low <= high)
            {
                var middle = (int)(((uint)low + (uint)high) >> 1);
                var comparison = SideOrder.Compare(side, levels[middle].Price, price);

                if (comparison == 0)
                {
                    exists = true;
                    return middle;
                }

                if (comparison < 0)
                    low = middle + 1;
                else
                    high = middle - 1;
            }

            exists = false;
            return low;
        }

        private readonly PriceLevel[][] _sides;
        private readonly int[] _counts;
    }
}
