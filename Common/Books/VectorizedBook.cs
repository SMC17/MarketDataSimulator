using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace MarketData.Common.Books
{
    /// <summary>
    /// A depth-limited book whose price search is a branch-free SIMD count rather than a search.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two changes to <see cref="SortedArrayBook"/>, both of which only pay off together.
    /// </para>
    /// <para>
    /// <b>Struct of arrays.</b> Prices live in one contiguous <c>int[]</c> and quantities in
    /// another, rather than interleaved in an array of 8-byte structs. Interleaved, a vector load
    /// of sixteen lanes would pull in eight prices and eight quantities and half the register would
    /// be waste; separated, one load is sixteen prices.
    /// </para>
    /// <para>
    /// <b>Prices stored as an order key.</b> Bids are inverted on the way in, so both sides ascend
    /// and touch-first is simply ascending order. That removes the per-comparison branch on side,
    /// and makes the position of a price the count of keys below it - a question SIMD answers
    /// directly.
    /// </para>
    /// <para>
    /// With both, locating a price is: one vector load, one compare, one mask extract, one
    /// population count. No branches, so no mispredictions - which is what actually costs a binary
    /// search at these sizes, since each of its steps depends on the previous one and the branch
    /// predictor has nothing to work with on random prices.
    /// </para>
    /// <para>
    /// The trailing slots are padded with <see cref="int.MaxValue"/>, so unused lanes can never
    /// compare below a real price and the loop needs no tail handling at all.
    /// </para>
    /// </remarks>
    public sealed class VectorizedBook : IOrderBook
    {
        public int Depth { get; }

        /// <summary>Widest vector the hardware will actually execute, in 32-bit lanes.</summary>
        public static int LaneCount => Vector512.IsHardwareAccelerated ? 16
            : Vector256.IsHardwareAccelerated ? 8
            : Vector128.IsHardwareAccelerated ? 4
            : 1;

        public VectorizedBook(int depth)
        {
            if (depth <= 0)
                throw new ArgumentOutOfRangeException(nameof(depth), depth, "Depth must be positive.");

            Depth = depth;

            // Capacity is rounded up to a whole number of vectors plus one spare slot, so an insert
            // into a full side can shift without a bounds check and every load stays in range.
            var capacity = ((depth + 1 + 15) / 16) * 16;

            _keys = new int[2][];
            _quantities = new uint[2][];
            _counts = new int[2];

            for (var side = 0; side < 2; side++)
            {
                _keys[side] = new int[capacity];
                _quantities[side] = new uint[capacity];
                _keys[side].AsSpan().Fill(int.MaxValue);
            }
        }

        public int Count(Side side) => _counts[(int)side];

        /// <summary>Maps a price to a key that sorts touch-first ascending on either side.</summary>
        /// <remarks>
        /// Bids invert with bitwise NOT rather than negation. Negation is not a bijection on
        /// <see cref="int"/>: <c>-int.MinValue</c> overflows back to <c>int.MinValue</c>, so a bid at
        /// that price produced the smallest key and sorted as the <em>best</em> bid instead of the
        /// worst - a crossed book, and a divergence from every other implementation. <c>~x</c> is
        /// order-reversing over the whole range, is its own inverse, and costs the same one
        /// instruction.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int ToKey(Side side, int price) => side == Side.Bid ? ~price : price;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int ToPrice(Side side, int key) => side == Side.Bid ? ~key : key;

        public bool TryGetBest(Side side, out PriceLevel level)
        {
            var index = (int)side;

            if (_counts[index] == 0)
            {
                level = default;
                return false;
            }

            level = new PriceLevel(ToPrice(side, _keys[index][0]), _quantities[index][0]);
            return true;
        }

        public bool TryGetQuantity(Side side, int price, out uint quantity)
        {
            var index = (int)side;
            var key = ToKey(side, price);
            var position = LowerBound(_keys[index], _counts[index], key);

            if (position < _counts[index] && _keys[index][position] == key)
            {
                quantity = _quantities[index][position];
                return true;
            }

            quantity = 0;
            return false;
        }

        public bool Upsert(Side side, int price, uint quantity)
        {
            if (quantity == 0)
                return Remove(side, price);

            var index = (int)side;
            var keys = _keys[index];
            var quantities = _quantities[index];
            var count = _counts[index];
            var key = ToKey(side, price);
            var position = LowerBound(keys, count, key);

            if (position < count && keys[position] == key)
            {
                if (quantities[position] == quantity)
                    return false;

                quantities[position] = quantity;
                return true;
            }

            if (count == Depth)
            {
                // Beyond the displayed window entirely.
                if (position == Depth)
                    return false;

                count--;
                keys[count] = int.MaxValue;
            }

            var tail = count - position;

            if (tail > 0)
            {
                Array.Copy(keys, position, keys, position + 1, tail);
                Array.Copy(quantities, position, quantities, position + 1, tail);
            }

            keys[position] = key;
            quantities[position] = quantity;
            _counts[index] = count + 1;
            return true;
        }

        public bool Remove(Side side, int price)
        {
            var index = (int)side;
            var keys = _keys[index];
            var count = _counts[index];
            var key = ToKey(side, price);
            var position = LowerBound(keys, count, key);

            if (position >= count || keys[position] != key)
                return false;

            var quantities = _quantities[index];
            var tail = count - position - 1;

            if (tail > 0)
            {
                Array.Copy(keys, position + 1, keys, position, tail);
                Array.Copy(quantities, position + 1, quantities, position, tail);
            }

            // Vacated slot returns to padding so it can never be counted as a real price again.
            keys[count - 1] = int.MaxValue;
            quantities[count - 1] = 0;
            _counts[index] = count - 1;
            return true;
        }

        /// <summary>
        /// Re-interleaves keys and quantities into levels.
        /// </summary>
        /// <remarks>
        /// This is what struct-of-arrays costs. An interleaved book publishes its top levels with a
        /// single memory copy; here they must be rebuilt element by element, and the price key
        /// un-negated on the way out. The sign is hoisted out of the loop and the spans are sliced
        /// once so the bounds checks are eliminated, but the work itself is inherent to the layout.
        /// </remarks>
        public int CopyTo(Side side, Span<PriceLevel> destination)
        {
            var index = (int)side;
            var count = Math.Min(_counts[index], destination.Length);

            if (count == 0)
                return 0;

            var keys = _keys[index].AsSpan(0, count);
            var quantities = _quantities[index].AsSpan(0, count);
            var levels = destination.Slice(0, count);

            // ToPrice rather than a hoisted sign multiply. The multiply was a second copy of the
            // key transform, and when the transform changed this copy silently kept the old one -
            // every price off by one. The branch inside ToPrice is loop-invariant and hoists.
            for (var i = 0; i < levels.Length; i++)
                levels[i] = new PriceLevel(ToPrice(side, keys[i]), quantities[i]);

            return count;
        }

        public void Clear()
        {
            for (var side = 0; side < 2; side++)
            {
                // Only the live prefix can hold real prices; the rest is already padding.
                _keys[side].AsSpan(0, _counts[side]).Fill(int.MaxValue);
                _counts[side] = 0;
            }
        }

        /// <summary>
        /// Levels up to which a branch-free vector scan beats a binary search.
        /// </summary>
        /// <remarks>
        /// The scan is O(n) with a very small constant; the search is O(log n) with a large one,
        /// because each step depends on the last and the branch predictor cannot guess prices. The
        /// scan therefore wins until n grows enough for the logarithm to matter. The crossover was
        /// measured, not guessed - see BENCHMARKS.md - and sits between 64 and 128 levels here.
        /// </remarks>
        private const int VectorScanLimit = 64;

        /// <summary>
        /// Index of the first key not below <paramref name="target"/>: the number of keys strictly
        /// below it.
        /// </summary>
        /// <remarks>
        /// Padding slots hold <see cref="int.MaxValue"/> and can never be counted, which is what
        /// lets the vector path run without tail handling.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int LowerBound(int[] keys, int count, int target)
        {
            if (count == 0)
                return 0;

            if (count > VectorScanLimit)
                return BinarySearchLowerBound(keys, count, target);

            if (Vector512.IsHardwareAccelerated)
                return CountBelow512(keys, count, target);

            if (Vector256.IsHardwareAccelerated)
                return CountBelow256(keys, count, target);

            return CountBelowScalar(keys, count, target);
        }

        /// <summary>Classic lower bound, used once the book is deep enough for O(log n) to pay.</summary>
        private static int BinarySearchLowerBound(int[] keys, int count, int target)
        {
            var low = 0;
            var high = count;

            while (low < high)
            {
                var middle = (int)(((uint)low + (uint)high) >> 1);

                if (keys[middle] < target)
                    low = middle + 1;
                else
                    high = middle;
            }

            return low;
        }

        private static int CountBelow512(int[] keys, int count, int target)
        {
            var needle = Vector512.Create(target);
            var below = 0;
            var i = 0;

            // Whole vectors first; padding guarantees these loads are in range.
            for (; i + Vector512<int>.Count <= keys.Length && i < count; i += Vector512<int>.Count)
            {
                var lanes = Vector512.LoadUnsafe(ref keys[i]);
                below += BitOperations.PopCount(Vector512.LessThan(lanes, needle).ExtractMostSignificantBits());
            }

            for (; i < count; i++)
            {
                if (keys[i] < target)
                    below++;
            }

            return below > count ? count : below;
        }

        private static int CountBelow256(int[] keys, int count, int target)
        {
            var needle = Vector256.Create(target);
            var below = 0;
            var i = 0;

            for (; i + Vector256<int>.Count <= keys.Length && i < count; i += Vector256<int>.Count)
            {
                var lanes = Vector256.LoadUnsafe(ref keys[i]);
                below += BitOperations.PopCount(Vector256.LessThan(lanes, needle).ExtractMostSignificantBits());
            }

            for (; i < count; i++)
            {
                if (keys[i] < target)
                    below++;
            }

            return below > count ? count : below;
        }

        private static int CountBelowScalar(int[] keys, int count, int target)
        {
            var below = 0;

            for (var i = 0; i < count; i++)
            {
                if (keys[i] < target)
                    below++;
            }

            return below;
        }

        private readonly int[][] _keys;
        private readonly uint[][] _quantities;
        private readonly int[] _counts;
    }
}
