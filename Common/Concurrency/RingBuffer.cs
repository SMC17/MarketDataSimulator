using System;
using System.Numerics;
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
        public const int MaxCapacity = 1 << 30;
        public int Capacity { get; }

        /// <summary>Items written since construction.</summary>
        public long Published => Volatile.Read(ref _write.Value);

        /// <summary>Items read since construction.</summary>
        public long Consumed => Volatile.Read(ref _read.Value);

        /// <summary>Items currently resident in the buffer, as an estimate.</summary>
        /// <remarks>
        /// <para>
        /// Two independent 64-bit loads, so the result belongs to no single instant. What it can be
        /// made to guarantee is that it stays inside <c>[0, Capacity]</c>, because a queue-depth
        /// metric that reports a negative backlog is worse than useless.
        /// </para>
        /// <para>
        /// The consumer cursor is read first, deliberately. Both cursors only ever increase and the
        /// producer is never behind the consumer, so a producer cursor read afterwards is at least
        /// as large as the consumer cursor was: the difference cannot come out negative. The
        /// opposite order has no such property - a consumer advancing between the loads yields a
        /// negative count outright.
        /// </para>
        /// <para>
        /// The residual error is one-sided and bounded: this order over-reports by however much the
        /// producer advanced between the loads, which the clamp caps at <see cref="Capacity"/>.
        /// </para>
        /// </remarks>
        public int Count
        {
            get
            {
                var consumed = Consumed;
                var published = Published;
                var count = published - consumed;

                if (count <= 0)
                    return 0;

                return count > Capacity ? Capacity : (int)count;
            }
        }

        public bool IsEmpty => Count == 0;

        /// <summary>Writes rejected because the buffer was full.</summary>
        public long Rejected => Volatile.Read(ref _rejected);

        public RingBuffer(int capacity)
        {
            if (capacity is <= 0 or > MaxCapacity)
                throw new ArgumentOutOfRangeException(nameof(capacity), capacity,
                    $"Capacity must be in [1, {MaxCapacity}], the largest representable power of two.");

            Capacity = (int)BitOperations.RoundUpToPowerOf2((uint)capacity);
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
        /// <returns>
        /// A span over the contiguous readable run. Empty when there is nothing to read.
        /// </returns>
        /// <remarks>
        /// A span rather than the backing array: handing out <c>_slots</c> itself let a caller read
        /// past the batch into slots the producer is concurrently writing, which is a torn read.
        /// The span cannot address them.
        /// </remarks>
        public ReadOnlySpan<T> PeekBatch()
        {
            var read = _read.Value;
            var available = Volatile.Read(ref _write.Value) - read;

            if (available <= 0)
                return ReadOnlySpan<T>.Empty;

            var start = (int)(read & _mask);
            var toEnd = Capacity - start;
            var length = available < toEnd ? (int)available : toEnd;

            return new ReadOnlySpan<T>(_slots, start, length);
        }

        /// <summary>Marks <paramref name="count"/> items consumed after a <see cref="PeekBatch"/>.</summary>
        /// <remarks>
        /// Over-releasing is unrecoverable, not merely wrong: the read cursor moves past the write
        /// cursor, every subsequent read reports the buffer empty forever, and nothing throws. It is
        /// worth a bounds check on the consumer's own arithmetic.
        /// </remarks>
        public void Release(int count)
        {
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count), count, "Count must not be negative.");

            var read = _read.Value;
            var available = Volatile.Read(ref _write.Value) - read;

            if (count > available)
                throw new ArgumentOutOfRangeException(nameof(count), count,
                    $"Cannot release {count} items; only {available} are unconsumed.");

            if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            {
                for (var i = 0; i < count; i++)
                    _slots[(read + i) & _mask] = default;
            }

            Volatile.Write(ref _read.Value, read + count);
        }

        private readonly T[] _slots;
        private readonly int _mask;

        // Each cursor sits in its own pair of cache lines; see PaddedLong.
        private PaddedLong _write;
        private PaddedLong _read;
        private long _rejected;
    }
}
