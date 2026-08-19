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
    }

    /// <summary>
    /// Turns received packets into book state, detecting loss along the way.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately owns no socket. Loss detection and recovery are the subtlest logic in a
    /// multicast consumer and the hardest to provoke over a real network, so they live in
    /// something that can be handed hostile packet sequences directly by a test - gaps,
    /// duplicates, reordering, truncation - with no timing involved.
    /// </para>
    /// <para>
    /// A multicast subscriber cannot ask for anything to be resent, so its only defence against
    /// loss is to notice it. Every packet carries the sequence of its first message; if that is
    /// not where the previous packet ended, messages were lost, and the difference is exactly how
    /// many.
    /// </para>
    /// <para>
    /// What happens next is the part that matters. Applying incrementals across a gap yields a
    /// book that is quietly wrong and stays wrong, which is far worse than admitting ignorance.
    /// So the consumer marks itself stale, ignores incrementals, and trusts the book again only
    /// once a full snapshot re-establishes a known state.
    /// </para>
    /// </remarks>
    public sealed class FeedDecoder
    {
        public FeedStatistics Statistics { get; } = new FeedStatistics();

        /// <summary>True while a detected gap has not yet been repaired by a snapshot.</summary>
        public bool IsStale => _stale;

        /// <summary>Sequence number the next packet is expected to start at.</summary>
        public ulong ExpectedSequence => _expected;

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

        public void Consume(ReadOnlySpan<byte> packet)
        {
            if (!FeedProtocol.TryReadHeader(packet, out var count, out var firstSequence, out var sourceTimestamp))
            {
                Interlocked.Increment(ref Statistics.Malformed);
                return;
            }

            Interlocked.Increment(ref Statistics.Packets);

            // Validate the whole packet before anything is committed. A packet that is truncated,
            // carries an unknown message type, or simply lies about its message count must be
            // discarded whole - if a bogus count were allowed to advance the expected sequence,
            // one corrupt or foreign datagram would desynchronise the consumer permanently and
            // every subsequent packet would be misread as a gap.
            if (!TryMeasure(packet, count, out var payloadLength))
            {
                Interlocked.Increment(ref Statistics.Malformed);
                return;
            }

            if (_started && firstSequence != _expected)
            {
                if (firstSequence < _expected)
                {
                    // Reordered or duplicated by the network; the book already reflects these.
                    Interlocked.Increment(ref Statistics.Duplicates);
                    return;
                }

                Interlocked.Increment(ref Statistics.Gaps);
                Interlocked.Add(ref Statistics.MissedMessages, (long)(firstSequence - _expected));
                _stale = true;
            }

            _started = true;
            _expected = firstSequence + count;

            var offset = FeedProtocol.HeaderSize;

            for (var i = 0; i < count; i++)
            {
                var remaining = packet.Slice(offset);
                Apply(remaining, sourceTimestamp);
                offset += FeedProtocol.MessageLength(remaining);
            }
        }

        /// <summary>
        /// Walks the packet without applying anything, confirming it holds exactly
        /// <paramref name="count"/> well-formed messages.
        /// </summary>
        private static bool TryMeasure(ReadOnlySpan<byte> packet, ushort count, out int payloadLength)
        {
            var offset = FeedProtocol.HeaderSize;

            for (var i = 0; i < count; i++)
            {
                if (offset >= packet.Length)
                {
                    payloadLength = 0;
                    return false;
                }

                var length = FeedProtocol.MessageLength(packet.Slice(offset));

                if (length < 0 || offset + length > packet.Length)
                {
                    payloadLength = 0;
                    return false;
                }

                offset += length;
            }

            payloadLength = offset;
            return true;
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

            if (_stale)
                return;

            FeedProtocol.ReadIncremental(message, out _, out var instrumentId, out var side, out var level);

            var target = BookFor(instrumentId);

            if (type == FeedMessageType.Remove)
                target.Remove(side, level.Price);
            else
                target.Upsert(side, level.Price, level.Quantity);
        }

        private readonly Func<int, IOrderBook> _bookFactory;
        private readonly Dictionary<int, IOrderBook> _books = new Dictionary<int, IOrderBook>();
        private readonly PriceLevel[] _bids = new PriceLevel[256];
        private readonly PriceLevel[] _asks = new PriceLevel[256];
        private ulong _expected;
        private bool _started;
        private bool _stale;
    }
}
