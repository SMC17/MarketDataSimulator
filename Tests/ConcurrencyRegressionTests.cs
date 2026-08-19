using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MarketData.Common.Concurrency;
using Xunit;

namespace MarketData.Tests
{
    /// <summary>
    /// Pins the defects an adversarial review of the lock-free dissemination path turned up.
    /// </summary>
    /// <remarks>
    /// Every test here failed before its fix. They are grouped rather than scattered because they
    /// share a theme: each was a place where code that <em>looked</em> atomic was not, or where a
    /// bad argument corrupted state silently instead of throwing.
    /// </remarks>
    public class ConcurrencyRegressionTests
    {
        /// <summary>
        /// Capacities that cannot be rounded up to a power of two must be rejected, not spun on.
        /// </summary>
        /// <remarks>
        /// The old round-up shifted left until it met or passed the request. Past 2^30 that shift
        /// overflows to int.MinValue and then to 0, and <c>0 &lt; value</c> is true forever - so the
        /// constructor hung the calling thread at full CPU instead of failing.
        /// </remarks>
        [Theory]
        [InlineData(int.MaxValue)]
        [InlineData((1 << 30) + 1)]
        public void CapacityAboveTheLargestPowerOfTwoIsRejected(int capacity)
        {
            var thrown = Assert.Throws<ArgumentOutOfRangeException>(() => new RingBuffer<int>(capacity));
            Assert.Contains("power of two", thrown.Message);
        }

        [Fact]
        public void TheLargestRepresentablePowerOfTwoIsStillAccepted()
            => Assert.Equal(1 << 30, new RingBuffer<byte>(1 << 30).Capacity);

        /// <summary>
        /// Releasing more than <see cref="RingBuffer{T}.PeekBatch"/> offered must throw.
        /// </summary>
        /// <remarks>
        /// Unchecked, an over-release moved the read cursor past the write cursor. Nothing threw;
        /// the ring simply reported itself permanently empty from then on and every subsequent
        /// message was lost. A silent unrecoverable state is worse than an exception.
        /// </remarks>
        [Fact]
        public void ReleasingMoreThanWasPeekedThrowsRatherThanCorruptingTheRing()
        {
            var ring = new RingBuffer<int>(8);

            for (var i = 0; i < 3; i++)
                ring.TryWrite(i);

            Assert.Equal(3, ring.PeekBatch().Length);
            Assert.Throws<ArgumentOutOfRangeException>(() => ring.Release(4));
            Assert.Throws<ArgumentOutOfRangeException>(() => ring.Release(-1));

            // The failed releases left the ring exactly as it was.
            Assert.Equal(3, ring.Count);
            Assert.Equal(new[] { 0, 1, 2 }, ring.PeekBatch().ToArray());
        }

        /// <summary>A batch view must not reach past the run it was granted.</summary>
        /// <remarks>
        /// The previous signature handed back the backing array itself, so a caller could index past
        /// the batch into slots the producer was concurrently writing. A span cannot address them.
        /// </remarks>
        [Fact]
        public void ABatchCannotSeePastItsOwnRun()
        {
            var ring = new RingBuffer<int>(8);

            for (var i = 0; i < 3; i++)
                ring.TryWrite(i);

            var batch = ring.PeekBatch();

            Assert.Equal(3, batch.Length);
            Assert.Throws<IndexOutOfRangeException>(() => _ = OutOfRange(ring, 3));
        }

        private static int OutOfRange(RingBuffer<int> ring, int index) => ring.PeekBatch()[index];

        /// <summary>
        /// <see cref="RingBuffer{T}.Count"/> is sampled while both cursors move and must never go
        /// negative.
        /// </summary>
        /// <remarks>
        /// It reads two independent 64-bit cursors, so it can never be exact. It can, however, be
        /// required to stay in range - and reading the producer's cursor first let a consumer
        /// advance in between and produce a negative count, which then flowed into the queue-depth
        /// metric the whole fan-out is judged by.
        /// </remarks>
        [Fact]
        public void CountNeverGoesNegativeWhileBothCursorsMove()
        {
            var ring = new RingBuffer<int>(1024);
            using var done = new CancellationTokenSource(TimeSpan.FromSeconds(2));

            var negatives = 0;
            var aboveCapacity = 0;

            var producer = Task.Run(() =>
            {
                while (!done.IsCancellationRequested)
                    ring.TryWrite(1);
            });

            var consumer = Task.Run(() =>
            {
                while (!done.IsCancellationRequested)
                    ring.TryRead(out _);
            });

            while (!done.IsCancellationRequested)
            {
                var count = ring.Count;

                if (count < 0)
                    negatives++;

                if (count > ring.Capacity)
                    aboveCapacity++;
            }

            Task.WaitAll(producer, consumer);

            Assert.Equal(0, negatives);
            Assert.Equal(0, aboveCapacity);
        }

        /// <summary>
        /// The backlog high-water mark must survive concurrent producers reporting depths.
        /// </summary>
        /// <remarks>
        /// This is the shape of the bug, reproduced on the primitive: read the current maximum, then
        /// store unconditionally. Two threads both observe a stale maximum and race to store, so the
        /// smaller value can land last and erase the larger. It under-reports the backlog precisely
        /// when the fan-out is falling behind - the one moment the number is worth reading.
        /// </remarks>
        [Fact]
        public void AHighWaterMarkNeedsCompareAndSwapNotReadThenExchange()
        {
            const int threads = 4;
            const int perThread = 20_000;

            var expected = (long)(threads * perThread);

            long correct = 0;
            using var start = new Barrier(threads);

            Parallel.For(0, threads, thread =>
            {
                start.SignalAndWait();

                for (var i = 1; i <= perThread; i++)
                {
                    var depth = (long)(thread * perThread + i);

                    long seen;
                    while (depth > (seen = Interlocked.Read(ref correct)))
                        Interlocked.CompareExchange(ref correct, depth, seen);
                }
            });

            Assert.Equal(expected, correct);
        }

        /// <summary>A queue with no producers yet must not block a consumer's shutdown.</summary>
        [Fact]
        public void TakingFromAnEmptyQueueHonoursTheCallersToken()
        {
            using var queue = new DisseminationQueue<int>();
            using var cancellation = new CancellationTokenSource();

            var ring = queue.AddProducer();
            var taken = Task.Run(() => queue.TryTake(out _, cancellation.Token));

            // Long enough for TryTake to exhaust its spin and yield budget and reach the blocking wait.
            Assert.False(taken.Wait(TimeSpan.FromMilliseconds(200)));

            cancellation.Cancel();

            Assert.True(taken.Wait(TimeSpan.FromSeconds(5)), "TryTake did not observe cancellation.");
            Assert.False(taken.Result);
        }

        /// <summary>Every producer's ring is drained, no matter how loud its neighbours are.</summary>
        /// <remarks>
        /// The consumer visits rings round-robin from a rolling cursor. A saturating producer must
        /// not be able to monopolise it, or one busy instrument would starve the rest of the feed.
        /// </remarks>
        [Fact]
        public void ALoudProducerCannotStarveAQuietOne()
        {
            using var queue = new DisseminationQueue<int>(1024);
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            var loud = queue.AddProducer();
            var quiet = queue.AddProducer();

            const int quietMessages = 200;
            var quietSeen = 0;
            var loudSeen = 0;

            var consumer = Task.Run(() =>
            {
                while (quietSeen < quietMessages && !shutdown.IsCancellationRequested)
                {
                    if (!queue.TryTake(out var value, shutdown.Token))
                        break;

                    if (value == 1)
                        quietSeen++;
                    else
                        loudSeen++;
                }
            });

            var loudProducer = Task.Run(() =>
            {
                while (!consumer.IsCompleted && !shutdown.IsCancellationRequested)
                {
                    loud.TryWrite(0);
                    queue.Signal();
                }
            });

            for (var i = 0; i < quietMessages; i++)
            {
                while (!quiet.TryWrite(1) && !shutdown.IsCancellationRequested)
                    Thread.Yield();

                queue.Signal();
                Thread.Sleep(1);
            }

            Assert.True(consumer.Wait(TimeSpan.FromSeconds(10)), "Consumer never drained the quiet producer.");
            loudProducer.Wait(TimeSpan.FromSeconds(5));

            Assert.Equal(quietMessages, quietSeen);
            Assert.True(loudSeen > 0, "The loud producer was not actually competing.");
        }

        /// <summary>Producers registered while the consumer is already draining are not missed.</summary>
        [Fact]
        public void ProducersAddedDuringDrainingAreStillServed()
        {
            using var queue = new DisseminationQueue<int>(64);
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            const int producers = 8;
            const int perProducer = 500;

            var received = new List<int>(producers * perProducer);

            var consumer = Task.Run(() =>
            {
                while (received.Count < producers * perProducer && !shutdown.IsCancellationRequested)
                {
                    if (!queue.TryTake(out var value, shutdown.Token))
                        break;

                    received.Add(value);
                }
            });

            var writers = Enumerable.Range(0, producers).Select(id => Task.Run(() =>
            {
                var ring = queue.AddProducer();

                for (var i = 0; i < perProducer; i++)
                {
                    while (!ring.TryWrite(id) && !shutdown.IsCancellationRequested)
                    {
                        queue.Signal();
                        Thread.Yield();
                    }

                    queue.Signal();
                }
            })).ToArray();

            Assert.True(Task.WaitAll(writers, TimeSpan.FromSeconds(10)), "Producers did not finish.");
            Assert.True(consumer.Wait(TimeSpan.FromSeconds(10)), "Consumer did not drain every producer.");

            Assert.Equal(producers * perProducer, received.Count);

            foreach (var group in received.GroupBy(id => id))
                Assert.Equal(perProducer, group.Count());
        }

        /// <summary>Depth is an estimate, but it must be a bounded one.</summary>
        [Fact]
        public void QueueDepthStaysInRange()
        {
            using var queue = new DisseminationQueue<int>(256);

            var a = queue.AddProducer();
            var b = queue.AddProducer();

            Assert.Equal(0, queue.Depth);

            for (var i = 0; i < 10; i++)
            {
                a.TryWrite(i);
                b.TryWrite(i);
            }

            Assert.Equal(20, queue.Depth);

            for (var i = 0; i < 20; i++)
                Assert.True(queue.TryTake(out _, CancellationToken.None));

            Assert.Equal(0, queue.Depth);
        }
    }
}
