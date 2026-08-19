using System;
using System.Threading;

namespace MarketData.Common.Durability
{
    /// <summary>
    /// Assigns the single global order that every downstream consumer agrees on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The sequence number is the contract. A subscriber detects loss because numbers skip, a
    /// backup takes over at a known point because the number is durable, and a retransmission
    /// request is expressible at all because ranges are named by it. Everything else in this
    /// namespace exists to keep that number meaningful.
    /// </para>
    /// <para>
    /// Sequences start at 1, so 0 is available as "nothing yet" without a nullable and without a
    /// sentinel that could collide with a real value.
    /// </para>
    /// </remarks>
    public sealed class Sequencer
    {
        /// <summary>The value meaning "no sequence has been assigned".</summary>
        public const ulong None = 0;

        private long _last;

        public Sequencer(ulong resumeFrom = None) => _last = (long)resumeFrom;

        /// <summary>The most recently assigned sequence, or <see cref="None"/>.</summary>
        public ulong Last => (ulong)Interlocked.Read(ref _last);

        /// <summary>Assigns the next sequence.</summary>
        /// <remarks>
        /// Interlocked rather than a plain increment because the sequencer is the one component
        /// several producers legitimately share - unlike the ring buffers downstream of it, which
        /// are single-producer by construction.
        /// </remarks>
        public ulong Next() => (ulong)Interlocked.Increment(ref _last);

        /// <summary>
        /// Reserves <paramref name="count"/> consecutive sequences and returns the first.
        /// </summary>
        /// <remarks>
        /// A batch published in one packet must occupy a contiguous range, or a subscriber that
        /// receives the packet would see a gap that never existed.
        /// </remarks>
        public ulong Reserve(int count)
        {
            if (count <= 0)
                throw new ArgumentOutOfRangeException(nameof(count), count, "Must reserve at least one.");

            var last = Interlocked.Add(ref _last, count);
            return (ulong)last - (ulong)count + 1;
        }

        /// <summary>
        /// Resumes from a recovered watermark, refusing to move backwards.
        /// </summary>
        /// <remarks>
        /// Rewinding a sequencer re-issues numbers that subscribers have already seen and applied,
        /// which is indistinguishable to them from a stuck feed and corrupts every book downstream.
        /// It is rejected rather than clamped so the caller finds out.
        /// </remarks>
        public void ResumeFrom(ulong sequence)
        {
            var current = Last;

            if (sequence < current)
                throw new ArgumentOutOfRangeException(nameof(sequence), sequence,
                    $"Refusing to rewind the sequencer from {current}; numbers would be reissued.");

            Interlocked.Exchange(ref _last, (long)sequence);
        }
    }
}
