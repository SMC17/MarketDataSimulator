using System;
using System.Numerics;

namespace MarketData.Common.Books
{
    /// <summary>
    /// Levels held in a price-indexed array - a "ladder" - with a bitset marking occupancy.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Locating a price is address arithmetic rather than a search: <c>price - minPrice</c> is the
    /// slot. Insert, update and delete are O(1) with no comparisons and no data movement, which is
    /// the property that matters when the book is deep.
    /// </para>
    /// <para>
    /// What that costs is the touch. Nothing about the array says where the best level is, so
    /// occupancy is mirrored into a bitset and the touch is found with a hardware bit-scan -
    /// <see cref="BitOperations.TrailingZeroCount(ulong)"/> and its leading counterpart - over
    /// 64 price slots per instruction. Touch and tail indices are cached and only rescanned when
    /// the level they point at is removed, so the scan is amortised close to nothing.
    /// </para>
    /// <para>
    /// The trade-off is generality: a ladder must be sized for a price band up front, and memory
    /// is proportional to that band rather than to the number of live levels. That is the right
    /// bargain for an instrument whose price moves within a known range over a session, and the
    /// wrong one for a sparse or unbounded price space. Prices outside the band are a programming
    /// error, not a runtime condition, and throw.
    /// </para>
    /// </remarks>
    public sealed class LadderBook : IOrderBook
    {
        public int Depth { get; }
        public int MinPrice { get; }
        public int MaxPrice { get; }

        public LadderBook(int depth, int minPrice, int maxPrice)
        {
            if (depth <= 0)
                throw new ArgumentOutOfRangeException(nameof(depth), depth, "Depth must be positive.");
            if (maxPrice < minPrice)
                throw new ArgumentException("maxPrice must not be below minPrice.", nameof(maxPrice));

            Depth = depth;
            MinPrice = minPrice;
            MaxPrice = maxPrice;

            _band = maxPrice - minPrice + 1;
            _index = new PriceIndex(_band);

            _quantities = new uint[2][];

            for (var side = 0; side < 2; side++)
                _quantities[side] = new uint[_band];
        }

        public int Count(Side side) => _index.Count(side);

        public bool TryGetBest(Side side, out PriceLevel level)
        {
            var slot = _index.Touch(side);

            if (slot == PriceIndex.None)
            {
                level = default;
                return false;
            }

            level = new PriceLevel(slot + MinPrice, _quantities[(int)side][slot]);
            return true;
        }

        public bool Upsert(Side side, int price, uint quantity)
        {
            if (quantity == 0)
                return Remove(side, price);

            var slot = ToSlot(price);
            var index = (int)side;

            if (_index.IsOccupied(side, slot))
            {
                if (_quantities[index][slot] == quantity)
                    return false;

                _quantities[index][slot] = quantity;
                return true;
            }

            if (_index.Count(side) == Depth)
            {
                // Worse than everything displayed: outside the window, so not a change.
                if (!PriceIndex.IsBetter(side, slot, _index.Tail(side)))
                    return false;

                RemoveAt(side, _index.Tail(side));
            }

            _quantities[index][slot] = quantity;
            _index.Occupy(side, slot);
            return true;
        }

        public bool Remove(Side side, int price)
        {
            var slot = ToSlot(price);

            if (!_index.IsOccupied(side, slot))
                return false;

            RemoveAt(side, slot);
            return true;
        }

        public int CopyTo(Side side, Span<PriceLevel> destination)
        {
            var remaining = Math.Min(_index.Count(side), destination.Length);

            if (remaining == 0)
                return 0;

            var quantities = _quantities[(int)side];
            var written = 0;
            var slot = _index.Touch(side);

            // Walk occupied slots outwards from the touch. Each step is a bit-scan, so the cost is
            // proportional to the band actually spanned rather than to the whole band.
            while (written < remaining && slot != PriceIndex.None)
            {
                destination[written++] = new PriceLevel(slot + MinPrice, quantities[slot]);
                slot = _index.Outward(side, slot);
            }

            return written;
        }

        public void Clear()
        {
            _index.Clear();

            for (var side = 0; side < 2; side++)
                Array.Clear(_quantities[side], 0, _quantities[side].Length);
        }

        private void RemoveAt(Side side, int slot)
        {
            _index.Vacate(side, slot);
            _quantities[(int)side][slot] = 0;
        }

        private int ToSlot(int price)
        {
            if (price < MinPrice || price > MaxPrice)
            {
                throw new ArgumentOutOfRangeException(nameof(price), price,
                    $"Price is outside the ladder's band [{MinPrice}, {MaxPrice}].");
            }

            return price - MinPrice;
        }

        private readonly int _band;
        private readonly PriceIndex _index;
        private readonly uint[][] _quantities;
    }
}
