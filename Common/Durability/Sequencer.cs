using System;
using System.Threading;

namespace MarketData.Common.Durability
{
    /// <summary>Lock-free monotonic sequence allocator. Sequence zero is the empty watermark.</summary>
    public sealed class Sequencer
    {
        public const ulong None = 0;

        // Interlocked has signed overloads; the bits remain an unsigned counter.
        private long _lastBits;

        public Sequencer(ulong resumeFrom = None) => _lastBits = unchecked((long)resumeFrom);

        public ulong Last => unchecked((ulong)Interlocked.Read(ref _lastBits));

        public ulong Next() => Reserve(1);

        /// <summary>Reserves a contiguous range and returns its first sequence.</summary>
        public ulong Reserve(int count)
        {
            if (count <= 0)
                throw new ArgumentOutOfRangeException(nameof(count), count, "Must reserve at least one.");

            while (true)
            {
                var observedBits = Interlocked.Read(ref _lastBits);
                var observed = unchecked((ulong)observedBits);

                if (observed > ulong.MaxValue - (uint)count)
                    throw new OverflowException("The sequence space is exhausted.");

                var next = observed + (uint)count;
                var nextBits = unchecked((long)next);

                if (Interlocked.CompareExchange(ref _lastBits, nextBits, observedBits) == observedBits)
                    return observed + 1;
            }
        }

        /// <summary>Advances to a recovered watermark; rewinds fail.</summary>
        public void ResumeFrom(ulong sequence)
        {
            while (true)
            {
                var observedBits = Interlocked.Read(ref _lastBits);
                var observed = unchecked((ulong)observedBits);

                if (sequence < observed)
                    throw new ArgumentOutOfRangeException(nameof(sequence), sequence,
                        $"Cannot rewind from {observed}.");
                if (sequence == observed)
                    return;

                if (Interlocked.CompareExchange(ref _lastBits, unchecked((long)sequence), observedBits) ==
                    observedBits)
                    return;
            }
        }
    }
}
