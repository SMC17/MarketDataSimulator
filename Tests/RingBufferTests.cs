using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MarketData.Common.Concurrency;
using Xunit;

namespace MarketData.Tests
{
    public class RingBufferTests
    {
        [Fact]
        public void CapacityRoundsUpToAPowerOfTwo()
        {
            Assert.Equal(1, new RingBuffer<int>(1).Capacity);
            Assert.Equal(8, new RingBuffer<int>(5).Capacity);
            Assert.Equal(1024, new RingBuffer<int>(1000).Capacity);
            Assert.Throws<ArgumentOutOfRangeException>(() => new RingBuffer<int>(0));
        }

        [Fact]
        public void ItemsComeBackInOrder()
        {
            var ring = new RingBuffer<int>(8);

            for (var i = 0; i < 8; i++)
                Assert.True(ring.TryWrite(i));

            for (var i = 0; i < 8; i++)
            {
                Assert.True(ring.TryRead(out var value));
                Assert.Equal(i, value);
            }

            Assert.False(ring.TryRead(out _));
        }

        [Fact]
        public void WritesAreRejectedWhenFullAndResumeAfterARead()
        {
            var ring = new RingBuffer<int>(4);

            for (var i = 0; i < 4; i++)
                Assert.True(ring.TryWrite(i));

            Assert.False(ring.TryWrite(99));
            Assert.Equal(1, ring.Rejected);
            Assert.Equal(4, ring.Count);

            Assert.True(ring.TryRead(out var first));
            Assert.Equal(0, first);
            Assert.True(ring.TryWrite(99));
        }

        [Fact]
        public void IndicesWrapCleanlyOverManyLaps()
        {
            var ring = new RingBuffer<int>(4);

            // Far more items than the capacity, so the mask arithmetic is exercised repeatedly.
            for (var i = 0; i < 10_000; i++)
            {
                Assert.True(ring.TryWrite(i));
                Assert.True(ring.TryRead(out var value));
                Assert.Equal(i, value);
            }

            Assert.True(ring.IsEmpty);
            Assert.Equal(10_000, ring.Published);
            Assert.Equal(10_000, ring.Consumed);
        }

        /// <summary>
        /// A consumed slot must not keep its object alive; otherwise a large ring looks like an
        /// unbounded leak that nothing else explains.
        /// </summary>
        [Fact]
        public void ConsumedSlotsReleaseTheirReferences()
        {
            var ring = new RingBuffer<object>(4);
            var reference = Publish(ring);

            // Consume it, so the only thing that could still reach the object is the ring's slot.
            Assert.True(ring.TryRead(out var item));
            Assert.NotNull(item);
            item = null;

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            Assert.False(reference.IsAlive, "the ring is still holding a consumed item");
        }

        /// <summary>
        /// Publishes an item from a frame that goes away, so no local in the caller can root it -
        /// otherwise the test would be measuring its own stack rather than the ring.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference Publish(RingBuffer<object> ring)
        {
            var item = new object();
            Assert.True(ring.TryWrite(item));
            return new WeakReference(item);
        }

        [Fact]
        public void BatchDrainReturnsAContiguousRun()
        {
            var ring = new RingBuffer<int>(8);

            for (var i = 0; i < 6; i++)
                ring.TryWrite(i);

            var batch = ring.PeekBatch();
            Assert.Equal(6, batch.Length);

            for (var i = 0; i < batch.Length; i++)
                Assert.Equal(i, batch[i]);

            ring.Release(batch.Length);
            Assert.True(ring.IsEmpty);
        }

        [Fact]
        public void BatchStopsAtTheWrapPoint()
        {
            var ring = new RingBuffer<int>(8);

            // Advance the cursors so the live run straddles the end of the array.
            for (var i = 0; i < 6; i++) { ring.TryWrite(i); ring.TryRead(out _); }
            for (var i = 0; i < 5; i++) ring.TryWrite(100 + i);

            var batch = ring.PeekBatch();

            // The run stops at the end of the array: two items now, the other three after the wrap.
            Assert.Equal(new[] { 100, 101 }, batch.ToArray());
            ring.Release(batch.Length);

            Assert.Equal(new[] { 102, 103, 104 }, ring.PeekBatch().ToArray());
        }

        [Fact]
        public void OperationsAllocateNothing()
        {
            var ring = new RingBuffer<long>(1024);

            for (var i = 0; i < 10_000; i++) { ring.TryWrite(i); ring.TryRead(out _); }

            var before = GC.GetAllocatedBytesForCurrentThread();

            for (var i = 0; i < 200_000; i++)
            {
                ring.TryWrite(i);
                ring.TryRead(out _);
            }

            var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(bytes == 0, $"expected zero allocation, measured {bytes} bytes");
        }

        /// <summary>
        /// The property the memory ordering exists for: with a real producer thread and a real
        /// consumer thread, every item must arrive exactly once and in order.
        /// </summary>
        /// <remarks>
        /// A missing release or acquire barrier lets the consumer observe a published index before
        /// the slot behind it is written, which surfaces as a torn or stale value - rarely, under
        /// load, and never in a debugger. Running a million items through a small ring makes the
        /// producer and consumer genuinely race for the same cache lines.
        /// </remarks>
        [Fact]
        public void SurvivesAConcurrentProducerAndConsumer()
        {
            const int items = 1_000_000;
            var ring = new RingBuffer<long>(1024);
            long consumed = 0;
            long outOfOrder = 0;

            var consumer = Task.Run(() =>
            {
                long expected = 0;

                while (expected < items)
                {
                    if (!ring.TryRead(out var value))
                    {
                        Thread.SpinWait(1);
                        continue;
                    }

                    if (value != expected)
                        Interlocked.Increment(ref outOfOrder);

                    expected++;
                    Interlocked.Increment(ref consumed);
                }
            });

            var producer = Task.Run(() =>
            {
                for (long i = 0; i < items; i++)
                {
                    while (!ring.TryWrite(i))
                        Thread.SpinWait(1);
                }
            });

            Assert.True(Task.WaitAll(new[] { producer, consumer }, TimeSpan.FromSeconds(60)),
                "producer and consumer did not finish; the ring may have deadlocked");

            Assert.Equal(0, Interlocked.Read(ref outOfOrder));
            Assert.Equal(items, Interlocked.Read(ref consumed));
            Assert.Equal(items, ring.Published);
            Assert.Equal(items, ring.Consumed);
        }

        [Fact]
        public void BatchDrainSurvivesAConcurrentProducer()
        {
            const int items = 500_000;
            var ring = new RingBuffer<long>(512);
            long outOfOrder = 0;

            var consumer = Task.Run(() =>
            {
                long expected = 0;

                while (expected < items)
                {
                    var batch = ring.PeekBatch();
                    var count = batch.Length;

                    if (count == 0)
                    {
                        Thread.SpinWait(1);
                        continue;
                    }

                    for (var i = 0; i < count; i++)
                    {
                        if (batch[i] != expected)
                            Interlocked.Increment(ref outOfOrder);

                        expected++;
                    }

                    ring.Release(count);
                }
            });

            var producer = Task.Run(() =>
            {
                for (long i = 0; i < items; i++)
                {
                    while (!ring.TryWrite(i))
                        Thread.SpinWait(1);
                }
            });

            Assert.True(Task.WaitAll(new[] { producer, consumer }, TimeSpan.FromSeconds(60)),
                "batch drain did not finish");

            Assert.Equal(0, Interlocked.Read(ref outOfOrder));
            Assert.Equal(items, ring.Consumed);
        }
    }
}
