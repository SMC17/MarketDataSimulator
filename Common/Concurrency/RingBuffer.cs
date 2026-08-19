using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace MarketData.Common.Concurrency
{
    /// <summary>
    /// A cache-line padded 64-bit counter.
    /// </summary>
    /// <remarks>
    /// The padding is the entire point. A producer's write index and a consumer's read index are
    /// touched by different cores on every single operation; placed adjacently they share a cache
    /// line, and each write invalidates the other core's copy even though neither core reads the
    /// other's value. That is false sharing, and it converts a lock-free queue into something
    /// slower than a locked one. Sixty-four bytes of separation costs nothing and removes it.
    /// </remarks>
    [StructLayout(LayoutKind.Explicit, Size = 128)]
    internal struct PaddedLong
    {
        [FieldOffset(64)]
        public long Value;
    }

    /// <summary>
    /// A bounded single-producer, single-consumer queue with no locks and no allocation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Replaces a general-purpose channel on the dissemination path. A channel has to be correct
    /// for many producers and many consumers, which means interlocked operations on a shared tail
    /// and a lock around the waiter list. This has exactly one producer and one consumer - the
    /// matching engine and the fan-out - so neither index needs an atomic read-modify-write at
    /// all. Each side owns its own cursor and only ever publishes it with a release store; the
    /// other side reads it with an acquire load. No CAS, no lock, no contention beyond the single
    /// cache line each cursor lives on.
    /// </para>
    /// <para>
    /// Capacity is rounded to a power of two so the wrap is a mask rather than a division, which
    /// matters when it happens on every element.
    /// </para>
    /// <para>
    /// Correctness rests on the memory ordering, not on the absence of a lock. The producer writes
    /// the slot and <em>then</em> publishes the index with <see cref="Volatile.Write"/>; the
    /// consumer reads the index with <see cref="Volatile.Read"/> and only then reads the slot.
    /// Without those, a compiler or a processor is free to reorder the store of the index ahead of
    /// the store of the data, and the consumer would read a slot that had not been filled yet. The
    /// bug that produces appears under load, on some machines, and not in a debugger.
    /// </para>
    /// </remarks>
    public sealed class RingBuffer<T>
    {
        public int Capacity { get; }

        /// <summary>Items written since construction.</summary>
        public long Published => Volatile.Read(ref _write.Value);

        /// <summary>Items read since construction.</summary>
        public long Consumed => Volatile.Read(ref _read.Value);

        public int Count => (int)(Published - Consumed);
        public bool IsEmpty => Published == Consumed;

        /// <summary>Writes rejected because the buffer was full.</summary>
        public long Rejected => Volatile.Read(ref _rejected);

        public RingBuffer(int capacity)
        {
            if (capacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be positive.");

            Capacity = RoundUpToPowerOfTwo(capacity);
            _mask = Capacity - 1;
            _slots = new T[Capacity];
        }

        /// <summary>
        /// Publishes one item. Producer side only.
        /// </summary>
        /// <returns><c>false</c> if the buffer is full; the item is not written.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryWrite(in T item)
        {
            // Only this thread advances the write cursor, so a plain read of it is safe and a
            // volatile one would be a needless barrier on the hot path.
            var write = _write.Value;

            // The consumer's cursor is written by the other thread, so this read must be acquire.
            if (write - Volatile.Read(ref _read.Value) >= Capacity)
            {
                Interlocked.Increment(ref _rejected);
                return false;
            }

            _slots[write & _mask] = item;

            // Release: the slot write above must not be reordered after this publication.
            Volatile.Write(ref _write.Value, write + 1);
            return true;
        }

        /// <summary>
        /// Takes one item. Consumer side only.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryRead(out T item)
        {
            var read = _read.Value;

            // Acquire: everything the producer wrote before publishing is visible after this read.
            if (read >= Volatile.Read(ref _write.Value))
            {
                item = default;
                return false;
            }

            var index = read & _mask;
            item = _slots[index];

            // Dropping the reference matters for reference types: a consumed slot that still
            // points at an object keeps it alive until the buffer wraps, which for a large ring
            // is an unbounded-looking leak that nothing else explains.
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
                _slots[index] = default;

            Volatile.Write(ref _read.Value, read + 1);
            return true;
        }

        /// <summary>
        /// Number of contiguous items readable without wrapping, and where they start.
        /// </summary>
        /// <remarks>
        /// Lets a consumer drain a run in one pass and publish its cursor once at the end, rather
        /// than paying a release store per item. Under load that is the difference between one
        /// cache-line handoff per batch and one per message.
        /// </remarks>
        public int PeekBatch(out T[] slots, out int start)
        {
            slots = _slots;
            var read = _read.Value;
            var available = (int)(Volatile.Read(ref _write.Value) - read);

            start = (int)(read & _mask);

            if (available <= 0)
                return 0;

            var toEnd = Capacity - start;
            return available < toEnd ? available : toEnd;
        }

        /// <summary>Marks <paramref name="count"/> items consumed after a <see cref="PeekBatch"/>.</summary>
        public void Release(int count)
        {
            var read = _read.Value;

            if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            {
                for (var i = 0; i < count; i++)
                    _slots[(read + i) & _mask] = default;
            }

            Volatile.Write(ref _read.Value, read + count);
        }

        private static int RoundUpToPowerOfTwo(int value)
        {
            var result = 1;

            while (result < value)
                result <<= 1;

            return result;
        }

        private readonly T[] _slots;
        private readonly int _mask;

        // Each cursor sits in its own pair of cache lines; see PaddedLong.
        private PaddedLong _write;
        private PaddedLong _read;
        private long _rejected;
    }
}
