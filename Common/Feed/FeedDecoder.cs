using MarketData.Common.Books;
using System;
using System.Collections.Generic;
using System.Threading;

namespace MarketData.Common.Feed
{
    /// <summary>Per-subscriber feed health, as a subscriber can actually observe it.</summary>
    public sealed class FeedStatistics
    {
        public long Packets;
        public long Messages;
        public long Gaps;
        public long MissedMessages;
        public long Duplicates;
        public long Malformed;
        public long Recoveries;
        public long Reordered;
    }

    /// <summary>
    /// Turns received packets into book state, detecting loss along the way.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately owns no socket. Loss detection, reordering and recovery are the subtlest logic
    /// in a multicast consumer and the hardest to provoke over a real network, so they live in
    /// something a test can hand hostile packet sequences directly, with no timing involved.
    /// </para>
    /// <para>
    /// A multicast subscriber cannot ask for anything to be resent, so its only defence against
    /// loss is to notice it. Every packet carries the sequence of its first message, which makes
    /// silent loss into detectable loss.
    /// </para>
    /// <para>
    /// What happens on noticing is the part that matters. Applying incrementals across a gap
    /// yields a book that is quietly wrong and stays wrong, which is far worse than admitting
    /// ignorance - so the consumer marks itself stale, ignores incrementals, and trusts the book
    /// again only once a full snapshot re-establishes a known state.
    /// </para>
    /// </remarks>
    public sealed class FeedDecoder
    {
        /// <summary>
        /// How many out-of-order packets may be held before the hole in front of them is ruled
        /// lost. Bounds both the memory a reordering burst can consume and how long a subscriber
        /// sits on a real gap before reporting it.
        /// </summary>
        public const int MaxHeldPackets = 64;

        public FeedStatistics Statistics { get; } = new FeedStatistics();

        /// <summary>True while a detected gap has not yet been repaired by a snapshot.</summary>
        public bool IsStale => _stale;

        /// <summary>Sequence number the next in-order packet should start at.</summary>
        public ulong ExpectedSequence => _expected;

        /// <summary>Out-of-order packets currently being held.</summary>
        public int HeldPackets => _held.Count;

        /// <summary>Raised per message with the publisher's transmit timestamp, for latency measurement.</summary>
        public event Action<long> MessageObserved;

        public FeedDecoder(Func<int, IOrderBook> bookFactory)
            => _bookFactory = bookFactory ?? throw new ArgumentNullException(nameof(bookFactory));

        public IOrderBook BookFor(int instrumentId)
        {
            if (_books.TryGetValue(instrumentId, out var book))
                return book;

            book = _bookFactory(instrumentId);
            _books[instrumentId] = book;
            return book;
        }

        /// <summary>
        /// Applies a received packet.
        /// </summary>
        /// <remarks>
        /// Locked because a redundant (A/B) subscriber feeds two independent sockets into one
        /// decoder. That arrangement is what makes line arbitration nearly free: the duplicate
        /// suppression already needed to survive network-level duplication is exactly the logic
        /// arbitration requires, so whichever copy arrives first is applied and the second is
        /// discarded.
        /// </remarks>
        public void Consume(ReadOnlySpan<byte> packet)
        {
            lock (_gate)
                ConsumeCore(packet);
        }

        private void ConsumeCore(ReadOnlySpan<byte> packet)
        {
            if (!FeedProtocol.TryReadHeader(packet, out var count, out var firstSequence, out var sourceTimestamp))
            {
                Interlocked.Increment(ref Statistics.Malformed);
                return;
            }

            // Validate the whole packet before anything is committed. A packet that is truncated,
            // carries an unknown message type, or simply lies about its message count must be
            // discarded entire - letting a bogus count advance the expected sequence would
            // desynchronise the consumer permanently on one corrupt or foreign datagram.
            if (!IsWellFormed(packet, count))
            {
                Interlocked.Increment(ref Statistics.Malformed);
                return;
            }

            Interlocked.Increment(ref Statistics.Packets);

            if (!_started)
            {
                _started = true;
                _expected = firstSequence;
            }

            if (firstSequence < _expected)
            {
                // Already consumed: a network duplicate, or the second copy on a redundant line.
                // Reapplying it would undo newer state.
                Interlocked.Increment(ref Statistics.Duplicates);
                return;
            }

            if (firstSequence == _expected)
            {
                ApplyPacket(packet, count, sourceTimestamp);
                _expected = firstSequence + count;
                Drain();
                return;
            }

            // Ahead of expectation. Loss and reordering are indistinguishable at this instant, so
            // hold the packet rather than guess. Reordering is normal on a redundant feed, where
            // the two lines have different path delays; declaring a gap on the first out-of-order
            // packet would report constant false loss.
            Hold(packet, firstSequence);

            if (_held.Count > MaxHeldPackets)
                DeclareGap();
        }

        private void Hold(ReadOnlySpan<byte> packet, ulong firstSequence)
        {
            if (_held.ContainsKey(firstSequence))
            {
                Interlocked.Increment(ref Statistics.Duplicates);
                return;
            }

            Interlocked.Increment(ref Statistics.Reordered);

            // Allocates, but only on the reordering path; the in-order path copies nothing.
            _held[firstSequence] = packet.ToArray();
        }

        /// <summary>
        /// Stops waiting for missing packets and resumes from the earliest one held, reporting the
        /// skipped range as lost.
        /// </summary>
        /// <remarks>
        /// The hold buffer alone is not enough to detect loss: on a slow feed a genuine gap might
        /// never be followed by the 64 further packets needed to fill the buffer, and the
        /// subscriber would wait indefinitely without reporting anything. A receive loop therefore
        /// calls this once packets have been held for longer than any plausible reordering delay -
        /// the gap timer that real feed handlers run.
        /// <para>
        /// Kept as an explicit call rather than an internal timer so the decoder stays free of
        /// wall-clock behaviour and its tests stay deterministic.
        /// </para>
        /// </remarks>
        public void FlushGaps()
        {
            lock (_gate)
            {
                // Repeats until nothing is held. The buffer can contain several holes, and
                // clearing only the first would leave the consumer quietly behind the feed while
                // reporting itself in sync - the exact failure mode staleness exists to prevent.
                while (_held.Count > 0)
                {
                    var before = _held.Count;

                    DeclareGap();

                    if (_held.Count >= before)
                        return; // no progress possible; avoid spinning
                }
            }
        }

        /// <summary>
        /// Stops waiting for missing packets and resumes from the earliest one held.
        /// </summary>
        /// <remarks>
        /// Once the hold buffer is full, everything before the earliest held sequence is treated
        /// as genuinely lost. That is the only safe reading: waiting longer would mean unbounded
        /// memory and unbounded delay for a subscriber that is, by then, certainly behind.
        /// </remarks>
        private void DeclareGap()
        {
            PruneOvertaken();

            var resume = ulong.MaxValue;

            foreach (var sequence in _held.Keys)
            {
                if (sequence < resume)
                    resume = sequence;
            }

            if (resume == ulong.MaxValue || resume <= _expected)
                return;

            Interlocked.Increment(ref Statistics.Gaps);
            Interlocked.Add(ref Statistics.MissedMessages, (long)(resume - _expected));
            _stale = true;
            _expected = resume;

            Drain();
        }

        /// <summary>Applies held packets that have become contiguous with the expected sequence.</summary>
        private void Drain()
        {
            while (true)
            {
                PruneOvertaken();

                if (!_held.TryGetValue(_expected, out var packet))
                    return;

                _held.Remove(_expected);

                FeedProtocol.TryReadHeader(packet, out var count, out _, out var sourceTimestamp);
                ApplyPacket(packet, count, sourceTimestamp);
                _expected += count;
            }
        }

        /// <summary>Discards held packets the in-order stream has since overtaken.</summary>
        private void PruneOvertaken()
        {
            if (_held.Count == 0)
                return;

            _overtaken.Clear();

            foreach (var sequence in _held.Keys)
            {
                if (sequence < _expected)
                    _overtaken.Add(sequence);
            }

            foreach (var sequence in _overtaken)
                _held.Remove(sequence);
        }

        private static bool IsWellFormed(ReadOnlySpan<byte> packet, ushort count)
        {
            var offset = FeedProtocol.HeaderSize;

            for (var i = 0; i < count; i++)
            {
                if (offset >= packet.Length)
                    return false;

                var length = FeedProtocol.MessageLength(packet.Slice(offset));

                if (length < 0 || offset + length > packet.Length)
                    return false;

                offset += length;
            }

            return true;
        }

        private void ApplyPacket(ReadOnlySpan<byte> packet, ushort count, long sourceTimestamp)
        {
            var offset = FeedProtocol.HeaderSize;

            for (var i = 0; i < count; i++)
            {
                var remaining = packet.Slice(offset);
                Apply(remaining, sourceTimestamp);
                offset += FeedProtocol.MessageLength(remaining);
            }
        }

        private void Apply(ReadOnlySpan<byte> message, long sourceTimestamp)
        {
            Interlocked.Increment(ref Statistics.Messages);
            MessageObserved?.Invoke(sourceTimestamp);

            var type = (FeedMessageType)message[0];

            if (type == FeedMessageType.Heartbeat)
                return;

            if (type == FeedMessageType.Snapshot)
            {
                FeedProtocol.ReadSnapshot(message, out var instrument, _bids, out var bidCount, _asks, out var askCount);

                var book = BookFor(instrument);
                book.Clear();

                for (var i = 0; i < bidCount; i++)
                    book.Upsert(Side.Bid, _bids[i].Price, _bids[i].Quantity);

                for (var i = 0; i < askCount; i++)
                    book.Upsert(Side.Ask, _asks[i].Price, _asks[i].Quantity);

                if (_stale)
                {
                    _stale = false;
                    Interlocked.Increment(ref Statistics.Recoveries);
                }

                return;
            }

            // A stale book must not absorb incrementals: applying them across a gap yields a book
            // that is wrong and gives no indication of it.
            if (_stale)
                return;

            FeedProtocol.ReadIncremental(message, out _, out var instrumentId, out var side, out var level);

            var target = BookFor(instrumentId);

            if (type == FeedMessageType.Remove)
                target.Remove(side, level.Price);
            else
                target.Upsert(side, level.Price, level.Quantity);
        }

        private readonly object _gate = new object();
        private readonly Func<int, IOrderBook> _bookFactory;
        private readonly Dictionary<int, IOrderBook> _books = new Dictionary<int, IOrderBook>();
        private readonly Dictionary<ulong, byte[]> _held = new Dictionary<ulong, byte[]>();
        private readonly List<ulong> _overtaken = new List<ulong>();
        private readonly PriceLevel[] _bids = new PriceLevel[256];
        private readonly PriceLevel[] _asks = new PriceLevel[256];
        private ulong _expected;
        private bool _started;
        private bool _stale;
    }
}
