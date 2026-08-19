using System;
using System.Numerics;

namespace MarketData.Common.Books
{
    /// <summary>
    /// Tracks which prices on each side currently hold anything, and where the touch and tail are.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Occupancy is a bitset over price slots, so finding the next occupied price is a hardware
    /// bit-scan covering 64 slots per instruction rather than a search. The touch and tail are
    /// cached and recomputed lazily, because they are read on every match and every publish but
    /// only change when a level appears or empties.
    /// </para>
    /// <para>
    /// This exists as one type because the caching is subtler than it looks and was got wrong
    /// twice. The sentinel means "invalidated, rescan on demand" - it does <em>not</em> mean "this
    /// side is empty", and code that conflates the two will happily adopt a newly inserted price
    /// as the touch without having looked at the rest of the side. The symptom is a book that
    /// silently hides its own best levels. Both the aggregated book and the matching engine need
    /// this logic; neither should own a second copy of it.
    /// </para>
    /// </remarks>
    public sealed class PriceIndex
    {
        public const int None = -1;

        public int Slots { get; }

        public PriceIndex(int slots)
        {
            if (slots <= 0)
                throw new ArgumentOutOfRangeException(nameof(slots), slots, "Slots must be positive.");

            Slots = slots;
            var words = (slots + 63) >> 6;

            _occupancy = new ulong[2][];
            _occupancy[0] = new ulong[words];
            _occupancy[1] = new ulong[words];
            _counts = new int[2];
            _touch = new[] { None, None };
            _tail = new[] { None, None };
        }

        /// <summary>Occupied price levels on a side.</summary>
        public int Count(Side side) => _counts[(int)side];

        public bool IsOccupied(Side side, int slot)
            => (_occupancy[(int)side][slot >> 6] & (1UL << (slot & 63))) != 0;

        /// <summary>Marks a slot occupied. Must not already be.</summary>
        public void Occupy(Side side, int slot)
        {
            var index = (int)side;
            _occupancy[index][slot >> 6] |= 1UL << (slot & 63);

            var count = ++_counts[index];

            if (count == 1)
            {
                // Genuinely the only level on this side, so it is both touch and tail.
                _touch[index] = slot;
                _tail[index] = slot;
                return;
            }

            // Otherwise refine only caches that are still valid. Adopting a slot from an
            // invalidated cache would claim it as the extreme without having looked at the rest.
            if (_touch[index] != None && IsBetter(side, slot, _touch[index]))
                _touch[index] = slot;

            if (_tail[index] != None && IsBetter(side, _tail[index], slot))
                _tail[index] = slot;
        }

        /// <summary>Marks a slot empty. Must currently be occupied.</summary>
        public void Vacate(Side side, int slot)
        {
            var index = (int)side;
            _occupancy[index][slot >> 6] &= ~(1UL << (slot & 63));
            _counts[index]--;

            if (_touch[index] == slot)
                _touch[index] = None;

            if (_tail[index] == slot)
                _tail[index] = None;
        }

        /// <summary>Best price on the side, or <see cref="None"/> when empty.</summary>
        public int Touch(Side side)
        {
            var index = (int)side;

            if (_counts[index] == 0)
                return None;

            if (_touch[index] != None)
                return _touch[index];

            var occupancy = _occupancy[index];
            var slot = side == Side.Bid ? Previous(occupancy, Slots - 1) : Next(occupancy, 0, Slots);

            _touch[index] = slot;
            return slot;
        }

        /// <summary>Worst occupied price on the side, or <see cref="None"/> when empty.</summary>
        public int Tail(Side side)
        {
            var index = (int)side;

            if (_counts[index] == 0)
                return None;

            if (_tail[index] != None)
                return _tail[index];

            var occupancy = _occupancy[index];
            var slot = side == Side.Bid ? Next(occupancy, 0, Slots) : Previous(occupancy, Slots - 1);

            _tail[index] = slot;
            return slot;
        }

        /// <summary>The next occupied slot moving away from the touch, or <see cref="None"/>.</summary>
        public int Outward(Side side, int slot)
        {
            var occupancy = _occupancy[(int)side];

            return side == Side.Bid ? Previous(occupancy, slot - 1) : Next(occupancy, slot + 1, Slots);
        }

        public void Clear()
        {
            for (var side = 0; side < 2; side++)
            {
                Array.Clear(_occupancy[side], 0, _occupancy[side].Length);
                _counts[side] = 0;
                _touch[side] = None;
                _tail[side] = None;
            }
        }

        /// <summary>True when <paramref name="candidate"/> is nearer the touch than <paramref name="incumbent"/>.</summary>
        public static bool IsBetter(Side side, int candidate, int incumbent)
            => side == Side.Bid ? candidate > incumbent : candidate < incumbent;

        private static int Next(ulong[] bits, int from, int slots)
        {
            if (from >= slots || from < 0)
                return None;

            var word = from >> 6;
            var masked = bits[word] & (ulong.MaxValue << (from & 63));

            while (true)
            {
                if (masked != 0)
                {
                    var slot = (word << 6) + BitOperations.TrailingZeroCount(masked);
                    return slot < slots ? slot : None;
                }

                if (++word >= bits.Length)
                    return None;

                masked = bits[word];
            }
        }

        private static int Previous(ulong[] bits, int from)
        {
            if (from < 0)
                return None;

            var word = from >> 6;
            var masked = bits[word] & (ulong.MaxValue >> (63 - (from & 63)));

            while (true)
            {
                if (masked != 0)
                    return (word << 6) + (63 - BitOperations.LeadingZeroCount(masked));

                if (--word < 0)
                    return None;

                masked = bits[word];
            }
        }

        private readonly ulong[][] _occupancy;
        private readonly int[] _counts;
        private readonly int[] _touch;
        private readonly int[] _tail;
    }
}
