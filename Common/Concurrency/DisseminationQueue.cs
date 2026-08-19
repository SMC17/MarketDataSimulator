using System;
using System.Collections.Generic;
using System.Threading;

namespace MarketData.Common.Concurrency
{
    /// <summary>
    /// The hand-off from the matching engines to the dissemination loop.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each producer gets its own single-producer ring, and the consumer drains them in turn. That
    /// keeps every ring genuinely SPSC - no interlocked read-modify-write on any cursor - where a
    /// single shared queue with several producers would need one per publish. Round-robin draining
    /// is also fairer than a shared queue under load, since a hot instrument cannot starve a quiet
    /// one out of its place in line.
    /// </para>
    /// <para>
    /// The wait strategy matters as much as the queue. Spinning is the fastest way to notice a new
    /// item and the most expensive way to wait for one, so this spins briefly, then yields, then
    /// blocks on an event the producers set. Busy at feed rate, idle at rest.
    /// </para>
    /// </remarks>
    public sealed class DisseminationQueue<T> : IDisposable
    {
        /// <summary>Spins before yielding. Long enough to cover a producer mid-publish, no longer.</summary>
        private const int SpinAttempts = 64;

        /// <summary>Yields before blocking, so a ready producer on another core gets a chance.</summary>
        private const int YieldAttempts = 8;

        public int ProducerCount => _rings.Count;

        public long Rejected
        {
            get
            {
                long total = 0;

                foreach (var ring in _rings)
                    total += ring.Rejected;

                return total;
            }
        }

        /// <summary>Total items resident across every producer's ring.</summary>
        /// <remarks>
        /// Sampled while producers and the consumer run, so this is an estimate: each ring's count
        /// is individually consistent but the sum spans no single instant.
        /// </remarks>
        public int Depth
        {
            get
            {
                var total = 0;

                foreach (var ring in _rings)
                    total += ring.Count;

                return total;
            }
        }

        public DisseminationQueue(int capacityPerProducer = 65_536) => _capacity = capacityPerProducer;

        /// <summary>
        /// Registers a producer and returns the ring it must publish through.
        /// </summary>
        /// <remarks>
        /// Called once per producer at start-up, never on the hot path - the returned ring is the
        /// only thing a producer touches afterwards, and nothing else may write to it.
        /// </remarks>
        public RingBuffer<T> AddProducer()
        {
            lock (_gate)
            {
                var ring = new RingBuffer<T>(_capacity);
                var rings = new List<RingBuffer<T>>(_rings) { ring };
                _rings = rings;
                return ring;
            }
        }

        /// <summary>Wakes a consumer that has gone to sleep. Producers call this after publishing.</summary>
        public void Signal() => _signal.Set();

        /// <summary>
        /// Takes the next item from any producer, waiting until one arrives or the token fires.
        /// </summary>
        public bool TryTake(out T item, CancellationToken token)
        {
            var spins = 0;
            var yields = 0;

            while (!token.IsCancellationRequested)
            {
                if (TryDrainOne(out item))
                    return true;

                if (spins++ < SpinAttempts)
                {
                    Thread.SpinWait(1 << Math.Min(spins, 6));
                    continue;
                }

                if (yields++ < YieldAttempts)
                {
                    Thread.Yield();
                    continue;
                }

                // Nothing to do: sleep until a producer says otherwise.
                //
                // Reset before the last emptiness check, not after the wait. Resetting afterwards
                // leaves the event set by a signal that arrived during the previous drain, so the
                // next Wait returns immediately and burns a whole spin-and-yield pass discovering
                // there is still nothing there. Clearing first and then re-checking cannot lose a
                // wake-up: a Set racing in after the Reset either lands before the check (we find
                // the item) or after it (the event is set, and Wait returns at once).
                _signal.Reset();

                if (TryDrainOne(out item))
                    return true;

                // The timeout is a belt-and-braces guard, and the token is honoured so shutdown is
                // not delayed by a full spin-and-yield cycle after the wait returns.
                try
                {
                    _signal.Wait(TimeSpan.FromMilliseconds(1), token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                spins = 0;
                yields = 0;
            }

            item = default;
            return false;
        }

        private bool TryDrainOne(out T item)
        {
            var rings = _rings;

            if (rings.Count == 0)
            {
                item = default;
                return false;
            }

            // Resume where the last drain stopped, so producers are served in rotation rather than
            // the first one always winning.
            for (var i = 0; i < rings.Count; i++)
            {
                var index = _cursor % rings.Count;
                _cursor = index + 1;

                if (rings[index].TryRead(out item))
                    return true;
            }

            item = default;
            return false;
        }

        public void Dispose() => _signal.Dispose();

        private readonly object _gate = new object();
        private readonly int _capacity;
        private readonly ManualResetEventSlim _signal = new ManualResetEventSlim(false);
        private volatile List<RingBuffer<T>> _rings = new List<RingBuffer<T>>();
        private int _cursor;
    }
}
