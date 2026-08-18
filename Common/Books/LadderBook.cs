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
            var words = (_band + 63) >> 6;

            _quantities = new uint[2][];
            _occupancy = new ulong[2][];

            for (var side = 0; side < 2; side++)
            {
                _quantities[side] = new uint[_band];
                _occupancy[side] = new ulong[words];
            }

            _counts = new int[2];
            _touch = new int[2] { Empty, Empty };
            _tail = new int[2] { Empty, Empty };
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

            var slot = TouchSlot(side);
            level = new PriceLevel(slot + MinPrice, _quantities[index][slot]);
            return true;
        }

        public bool Upsert(Side side, int price, uint quantity)
        {
            if (quantity == 0)
                return Remove(side, price);

            var slot = ToSlot(price);
            var index = (int)side;

            if (IsSet(_occupancy[index], slot))
            {
                if (_quantities[index][slot] == quantity)
                    return false;

                _quantities[index][slot] = quantity;
                return true;
            }

            if (_counts[index] == Depth)
            {
                var tail = TailSlot(side);

                // Worse than everything displayed: outside the window, so not a change.
                if (!IsBetterSlot(side, slot, tail))
                    return false;

                RemoveAt(index, tail);
            }

            _quantities[index][slot] = quantity;
            Set(_occupancy[index], slot);
            var count = ++_counts[index];

            if (count == 1)
            {
                _touch[index] = slot;
                _tail[index] = slot;
                return true;
            }

            // Refine only caches that are still valid. Empty means "invalidated, recompute on
            // demand", not "no levels" - adopting the new slot from an invalidated cache would
            // claim a level is the touch or the tail without having looked at the rest of the
            // side, which silently corrupts the depth cap.
            if (_touch[index] != Empty && IsBetterSlot(side, slot, _touch[index]))
                _touch[index] = slot;

            if (_tail[index] != Empty && IsBetterSlot(side, _tail[index], slot))
                _tail[index] = slot;

            return true;
        }

        public bool Remove(Side side, int price)
        {
            var slot = ToSlot(price);
            var index = (int)side;

            if (!IsSet(_occupancy[index], slot))
                return false;

            RemoveAt(index, slot);
            return true;
        }

        public int CopyTo(Side side, Span<PriceLevel> destination)
        {
            var index = (int)side;
            var remaining = Math.Min(_counts[index], destination.Length);

            if (remaining == 0)
                return 0;

            var quantities = _quantities[index];
            var occupancy = _occupancy[index];
            var written = 0;
            var slot = TouchSlot(side);

            // Walk occupied slots outwards from the touch. Each step is a bit-scan, so the cost is
            // proportional to the band actually spanned rather than to the whole band.
            while (written < remaining && slot != Empty)
            {
                destination[written++] = new PriceLevel(slot + MinPrice, quantities[slot]);
                slot = side == Side.Bid
                    ? PreviousSet(occupancy, slot - 1)
                    : NextSet(occupancy, slot + 1, _band);
            }

            return written;
        }

        public void Clear()
        {
            for (var side = 0; side < 2; side++)
            {
                Array.Clear(_quantities[side], 0, _quantities[side].Length);
                Array.Clear(_occupancy[side], 0, _occupancy[side].Length);
                _counts[side] = 0;
                _touch[side] = Empty;
                _tail[side] = Empty;
            }
        }

        private void RemoveAt(int index, int slot)
        {
            Unset(_occupancy[index], slot);
            _quantities[index][slot] = 0;
            _counts[index]--;

            // Only the cached endpoints need invalidating; they are recomputed lazily on demand.
            if (_touch[index] == slot)
                _touch[index] = Empty;

            if (_tail[index] == slot)
                _tail[index] = Empty;
        }

        private int TouchSlot(Side side)
        {
            var index = (int)side;
            var cached = _touch[index];

            if (cached != Empty)
                return cached;

            var occupancy = _occupancy[index];
            var slot = side == Side.Bid ? PreviousSet(occupancy, _band - 1) : NextSet(occupancy, 0, _band);

            _touch[index] = slot;
            return slot;
        }

        private int TailSlot(Side side)
        {
            var index = (int)side;
            var cached = _tail[index];

            if (cached != Empty)
                return cached;

            var occupancy = _occupancy[index];
            var slot = side == Side.Bid ? NextSet(occupancy, 0, _band) : PreviousSet(occupancy, _band - 1);

            _tail[index] = slot;
            return slot;
        }

        /// <summary>True when <paramref name="candidate"/> is closer to the touch than <paramref name="incumbent"/>.</summary>
        private static bool IsBetterSlot(Side side, int candidate, int incumbent)
            => side == Side.Bid ? candidate > incumbent : candidate < incumbent;

        private int ToSlot(int price)
        {
            if (price < MinPrice || price > MaxPrice)
            {
                throw new ArgumentOutOfRangeException(nameof(price), price,
                    $"Price is outside the ladder's band [{MinPrice}, {MaxPrice}].");
            }

            return price - MinPrice;
        }

        private static bool IsSet(ulong[] bits, int slot) => (bits[slot >> 6] & (1UL << (slot & 63))) != 0;
        private static void Set(ulong[] bits, int slot) => bits[slot >> 6] |= 1UL << (slot & 63);
        private static void Unset(ulong[] bits, int slot) => bits[slot >> 6] &= ~(1UL << (slot & 63));

        /// <summary>Lowest set bit at or above <paramref name="from"/>, or <see cref="Empty"/>.</summary>
        private static int NextSet(ulong[] bits, int from, int band)
        {
            if (from >= band)
                return Empty;

            var word = from >> 6;
            var masked = bits[word] & (ulong.MaxValue << (from & 63));

            while (true)
            {
                if (masked != 0)
                {
                    var slot = (word << 6) + BitOperations.TrailingZeroCount(masked);
                    return slot < band ? slot : Empty;
                }

                if (++word >= bits.Length)
                    return Empty;

                masked = bits[word];
            }
        }

        /// <summary>Highest set bit at or below <paramref name="from"/>, or <see cref="Empty"/>.</summary>
        private static int PreviousSet(ulong[] bits, int from)
        {
            if (from < 0)
                return Empty;

            var word = from >> 6;
            var shift = 63 - (from & 63);
            var masked = bits[word] & (ulong.MaxValue >> shift);

            while (true)
            {
                if (masked != 0)
                    return (word << 6) + (63 - BitOperations.LeadingZeroCount(masked));

                if (--word < 0)
                    return Empty;

                masked = bits[word];
            }
        }

        private const int Empty = -1;

        private readonly int _band;
        private readonly uint[][] _quantities;
        private readonly ulong[][] _occupancy;
        private readonly int[] _counts;
        private readonly int[] _touch;
        private readonly int[] _tail;
    }
}
