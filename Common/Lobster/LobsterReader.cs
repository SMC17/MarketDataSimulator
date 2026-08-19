using System;
using System.Buffers.Text;
using System.Runtime.CompilerServices;

namespace MarketData.Common.Lobster
{
    /// <summary>
    /// Parses LOBSTER CSV directly out of a byte buffer, without allocating.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The obvious implementation - <c>ReadLine</c>, <c>Split(',')</c>, <c>int.Parse</c> - allocates
    /// a string for the line and one per field, so a 270,000-row file becomes millions of short
    /// lived objects and the parse becomes a garbage collection benchmark. This works on
    /// <see cref="ReadOnlySpan{T}"/> over the raw bytes throughout: no strings, no substrings, no
    /// decoding, nothing on the heap.
    /// </para>
    /// <para>
    /// Field splitting uses <see cref="System.MemoryExtensions.IndexOf{T}(ReadOnlySpan{T}, T)"/>,
    /// which the runtime vectorises - the delimiter search runs over a whole SIMD register at a
    /// time rather than byte by byte.
    /// </para>
    /// </remarks>
    public ref struct LobsterReader
    {
        private ReadOnlySpan<byte> _remaining;

        public LobsterReader(ReadOnlySpan<byte> content) => _remaining = content;

        /// <summary>Reads the next message, or returns false at end of input.</summary>
        public bool TryReadMessage(out LobsterMessage message)
        {
            message = default;

            if (!TryTakeLine(out var line))
                return false;

            // Time,Type,OrderID,Size,Price,Direction
            if (!TryTakeField(ref line, out var timeField) ||
                !TryTakeField(ref line, out var typeField) ||
                !TryTakeField(ref line, out var idField) ||
                !TryTakeField(ref line, out var sizeField) ||
                !TryTakeField(ref line, out var priceField))
            {
                return false;
            }

            var directionField = line;

            message = new LobsterMessage(
                ParseSecondsAsNanoseconds(timeField),
                (LobsterEventType)ParseInt64(typeField),
                ParseInt64(idField),
                (uint)ParseInt64(sizeField),
                (int)ParseInt64(priceField),
                (sbyte)ParseInt64(directionField));

            return true;
        }

        /// <summary>
        /// Reads one row of an orderbook file into <paramref name="levels"/> as
        /// (askPrice, askSize, bidPrice, bidSize) repeated per level.
        /// </summary>
        /// <remarks>
        /// <paramref name="levels"/> is <c>scoped</c>: this type is a <c>ref struct</c> holding a
        /// span, so without that promise the compiler must assume the reader could retain the
        /// caller's buffer, and would refuse a stack-allocated one. Saying it explicitly keeps the
        /// caller's row on the stack rather than forcing it onto the heap.
        /// </remarks>
        public bool TryReadBookRow(scoped Span<int> levels, out int count)
        {
            count = 0;

            if (!TryTakeLine(out var line))
                return false;

            while (count < levels.Length)
            {
                if (!TryTakeField(ref line, out var field))
                {
                    // Final field on the row carries no trailing comma.
                    if (line.Length > 0)
                        levels[count++] = (int)ParseInt64(line);

                    break;
                }

                levels[count++] = (int)ParseInt64(field);
            }

            return count > 0;
        }

        private bool TryTakeLine(out ReadOnlySpan<byte> line)
        {
            if (_remaining.Length == 0)
            {
                line = default;
                return false;
            }

            var end = _remaining.IndexOf((byte)'\n');

            if (end < 0)
            {
                line = _remaining;
                _remaining = default;
            }
            else
            {
                line = _remaining.Slice(0, end);
                _remaining = _remaining.Slice(end + 1);
            }

            // Tolerate CRLF without a second pass over the line.
            if (line.Length > 0 && line[line.Length - 1] == (byte)'\r')
                line = line.Slice(0, line.Length - 1);

            return line.Length > 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TryTakeField(ref ReadOnlySpan<byte> line, out ReadOnlySpan<byte> field)
        {
            var comma = line.IndexOf((byte)',');

            if (comma < 0)
            {
                field = default;
                return false;
            }

            field = line.Slice(0, comma);
            line = line.Slice(comma + 1);
            return true;
        }

        /// <summary>Parses a signed integer without allocating or going through a string.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static long ParseInt64(ReadOnlySpan<byte> field)
        {
            if (field.Length == 0)
                return 0;

            var negative = field[0] == (byte)'-';
            var i = negative ? 1 : 0;
            long value = 0;

            for (; i < field.Length; i++)
            {
                var digit = field[i] - (byte)'0';

                if ((uint)digit > 9)
                    break;

                value = value * 10 + digit;
            }

            return negative ? -value : value;
        }

        /// <summary>
        /// Parses "seconds.fraction" into integer nanoseconds since midnight.
        /// </summary>
        /// <remarks>
        /// Deliberately not via <c>double</c>. LOBSTER timestamps carry nine fractional digits, and
        /// a double has about fifteen significant decimal digits total - enough to represent
        /// 34200.123456789 only barely, and not enough to survive arithmetic on it. Integer
        /// nanoseconds are exact, and exactness is what lets two events be ordered confidently.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static long ParseSecondsAsNanoseconds(ReadOnlySpan<byte> field)
        {
            var dot = field.IndexOf((byte)'.');

            if (dot < 0)
                return ParseInt64(field) * 1_000_000_000L;

            var seconds = ParseInt64(field.Slice(0, dot));
            var fraction = field.Slice(dot + 1);

            long nanoseconds = 0;
            var digits = 0;

            for (; digits < fraction.Length && digits < 9; digits++)
            {
                var digit = fraction[digits] - (byte)'0';

                if ((uint)digit > 9)
                    break;

                nanoseconds = nanoseconds * 10 + digit;
            }

            // Scale up when fewer than nine fractional digits were present.
            for (var scale = digits; scale < 9; scale++)
                nanoseconds *= 10;

            return seconds * 1_000_000_000L + nanoseconds;
        }
    }
}
