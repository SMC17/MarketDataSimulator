using System.Buffers.Binary;
using System.Threading;
using MarketData.Common.Books;

namespace MarketData.Common.Feed
{
    public sealed class FeedStatistics
    {
        public long Packets;
        public long Messages;
        public long Gaps;
        public long MissedMessages;
        public long Duplicates;
        public long LineDivergences;
        public long Malformed;
        public long IntegrityFailures;
        public long Recoveries;
        public long Reordered;
        public long SessionChanges;
        public long OldSessionPackets;
        public long IgnoredIncrementals;
    }

    public readonly record struct FeedGap(ulong SessionId, ulong ExpectedSequence,
        ulong ResumeSequence, ulong MissedMessages);

    /// <summary>Deterministic feed state machine for sequencing, arbitration, and recovery.</summary>
    public sealed class FeedDecoder
    {
        public const int MaxHeldPackets = 64;
        private const int RetiredSessionLimit = 8;

        public FeedStatistics Statistics { get; } = new FeedStatistics();

        public bool IsStale
        {
            get
            {
                lock (_gate)
                {
                    if (!_started || _books.Count == 0)
                        return true;

                    foreach (var state in _books.Values)
                        if (state.Generation != _generation)
                            return true;

                    return false;
                }
            }
        }

        public ulong ExpectedSequence
        {
            get { lock (_gate) return _expected; }
        }

        public ulong SessionId
        {
            get { lock (_gate) return _sessionId; }
        }

        public int HeldPackets
        {
            get { lock (_gate) return _held.Count; }
        }

        public event Action<long> MessageObserved;

        /// <summary>Recovery hook for retransmission or snapshot-request infrastructure.</summary>
        public event Action<FeedGap> GapDetected;

        public FeedDecoder(Func<int, IOrderBook> bookFactory)
            => _bookFactory = bookFactory ?? throw new ArgumentNullException(nameof(bookFactory));

        public IOrderBook BookFor(int instrumentId)
        {
            lock (_gate)
                return StateFor(instrumentId).Book;
        }

        public bool IsInstrumentStale(int instrumentId)
        {
            lock (_gate)
                return !_started || !_books.TryGetValue(instrumentId, out var state) ||
                    state.Generation != _generation;
        }

        public void Consume(ReadOnlySpan<byte> packet)
        {
            lock (_gate)
                ConsumeCore(packet);
        }

        private void ConsumeCore(ReadOnlySpan<byte> packet)
        {
            if (!FeedProtocol.TryReadHeader(packet, out var header, out var error))
            {
                Interlocked.Increment(ref Statistics.Malformed);

                if (error == FeedProtocolError.Checksum)
                    Interlocked.Increment(ref Statistics.IntegrityFailures);

                return;
            }

            if (!IsWellFormed(packet, header.MessageCount))
            {
                Interlocked.Increment(ref Statistics.Malformed);
                return;
            }

            Interlocked.Increment(ref Statistics.Packets);

            if (!_started)
            {
                BeginSession(header.SessionId, header.FirstSequence);
            }
            else if (header.SessionId != _sessionId)
            {
                if (_retiredSessions.Contains(header.SessionId))
                {
                    Interlocked.Increment(ref Statistics.OldSessionPackets);
                    return;
                }

                // A publisher always starts at zero. Requiring that boundary prevents a stray
                // datagram from an unrelated publisher from taking over a live decoder.
                if (header.FirstSequence != 0)
                {
                    Interlocked.Increment(ref Statistics.OldSessionPackets);
                    return;
                }

                RetireCurrentSession();
                BeginSession(header.SessionId, 0);
                Interlocked.Increment(ref Statistics.SessionChanges);
            }

            if (header.FirstSequence < _expected)
            {
                Interlocked.Increment(ref Statistics.Duplicates);

                if (_recent.TryGetValue(header.FirstSequence, out var identity) &&
                    !identity.Matches(packet, header))
                    Interlocked.Increment(ref Statistics.LineDivergences);

                return;
            }

            if (header.FirstSequence == _expected)
            {
                ApplyPacket(packet, header);
                Remember(packet, header);
                _expected = header.FirstSequence + header.MessageCount;
                Drain();
                return;
            }

            Hold(packet, header.FirstSequence);

            if (_held.Count > MaxHeldPackets)
                DeclareGap();
        }

        private void BeginSession(ulong sessionId, ulong firstSequence)
        {
            _started = true;
            _sessionId = sessionId;
            _expected = firstSequence;
            _held.Clear();
            _recent.Clear();
            _recentOrder.Clear();
            AdvanceGeneration();
        }

        private void RetireCurrentSession()
        {
            if (_retiredSessions.Add(_sessionId))
                _retiredSessionOrder.Enqueue(_sessionId);

            while (_retiredSessionOrder.Count > RetiredSessionLimit)
                _retiredSessions.Remove(_retiredSessionOrder.Dequeue());
        }

        private void AdvanceGeneration()
        {
            if (_generation == ulong.MaxValue)
            {
                foreach (var state in _books.Values)
                    state.Generation = 0;

                _generation = 1;
                return;
            }

            _generation++;
        }

        private void Hold(ReadOnlySpan<byte> packet, ulong firstSequence)
        {
            if (_held.TryGetValue(firstSequence, out var existing))
            {
                Interlocked.Increment(ref Statistics.Duplicates);

                if (!packet.SequenceEqual(existing))
                    Interlocked.Increment(ref Statistics.LineDivergences);

                return;
            }

            Interlocked.Increment(ref Statistics.Reordered);
            _held[firstSequence] = packet.ToArray();
        }

        public void FlushGaps()
        {
            lock (_gate)
            {
                while (_held.Count > 0)
                {
                    var before = _held.Count;
                    DeclareGap();

                    if (_held.Count >= before)
                        return;
                }
            }
        }

        private void DeclareGap()
        {
            PruneOvertaken();

            var resume = ulong.MaxValue;

            foreach (var sequence in _held.Keys)
                if (sequence < resume)
                    resume = sequence;

            if (resume == ulong.MaxValue || resume <= _expected)
                return;

            var expected = _expected;
            var missed = resume - expected;

            Interlocked.Increment(ref Statistics.Gaps);
            Interlocked.Add(ref Statistics.MissedMessages,
                missed > long.MaxValue ? long.MaxValue : (long)missed);
            AdvanceGeneration();
            _expected = resume;

            NotifyGap(new FeedGap(_sessionId, expected, resume, missed));
            Drain();
        }

        private void NotifyGap(FeedGap gap)
        {
            var handlers = GapDetected;

            if (handlers is null)
                return;

            foreach (Action<FeedGap> handler in handlers.GetInvocationList())
            {
                try { handler(gap); }
                catch { /* diagnostics must not compromise feed state */ }
            }
        }

        private void Drain()
        {
            while (true)
            {
                PruneOvertaken();

                if (!_held.TryGetValue(_expected, out var packet))
                    return;

                _held.Remove(_expected);
                FeedProtocol.TryReadHeader(packet, out var header, out _);
                ApplyPacket(packet, header);
                Remember(packet, header);
                _expected += header.MessageCount;
            }
        }

        private void PruneOvertaken()
        {
            if (_held.Count == 0)
                return;

            _overtaken.Clear();

            foreach (var sequence in _held.Keys)
                if (sequence < _expected)
                    _overtaken.Add(sequence);

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

                var message = packet.Slice(offset);
                var length = FeedProtocol.MessageLength(message);

                if (length < 0 || offset + length > packet.Length)
                    return false;
                if (message[0] == (byte)FeedMessageType.Snapshot && !IsValidSnapshot(message.Slice(0, length)))
                    return false;

                offset += length;
            }

            return offset == packet.Length;
        }

        private static bool IsValidSnapshot(ReadOnlySpan<byte> message)
        {
            var bidCount = message[5];
            var askCount = message[6];
            var offset = 7;
            var previous = 0;
            var bestBid = 0;
            var bestAsk = 0;

            for (var i = 0; i < bidCount; i++, offset += 8)
            {
                var price = BinaryPrimitives.ReadInt32LittleEndian(message.Slice(offset, 4));
                var quantity = BinaryPrimitives.ReadUInt32LittleEndian(message.Slice(offset + 4, 4));

                if (quantity == 0 || (i > 0 && price >= previous))
                    return false;

                if (i == 0)
                    bestBid = price;

                previous = price;
            }

            previous = 0;

            for (var i = 0; i < askCount; i++, offset += 8)
            {
                var price = BinaryPrimitives.ReadInt32LittleEndian(message.Slice(offset, 4));
                var quantity = BinaryPrimitives.ReadUInt32LittleEndian(message.Slice(offset + 4, 4));

                if (quantity == 0 || (i > 0 && price <= previous))
                    return false;

                if (i == 0)
                    bestAsk = price;

                previous = price;
            }

            return bidCount == 0 || askCount == 0 || bestBid < bestAsk;
        }

        private void ApplyPacket(ReadOnlySpan<byte> packet, FeedHeader header)
        {
            var offset = FeedProtocol.HeaderSize;

            for (var i = 0; i < header.MessageCount; i++)
            {
                var message = packet.Slice(offset);
                Apply(message, header.SourceTimestamp);
                offset += FeedProtocol.MessageLength(message);
            }
        }

        private void Apply(ReadOnlySpan<byte> message, long sourceTimestamp)
        {
            Interlocked.Increment(ref Statistics.Messages);

            try { MessageObserved?.Invoke(sourceTimestamp); }
            catch { /* observers cannot compromise feed state */ }

            var type = (FeedMessageType)message[0];

            if (type == FeedMessageType.Heartbeat)
                return;

            if (type == FeedMessageType.Snapshot)
            {
                FeedProtocol.ReadSnapshot(message, out var instrument, _bids, out var bidCount,
                    _asks, out var askCount);

                var state = StateFor(instrument);
                var wasStale = state.Generation != _generation;
                state.Book.Clear();

                for (var i = 0; i < bidCount; i++)
                    state.Book.Upsert(Side.Bid, _bids[i].Price, _bids[i].Quantity);

                for (var i = 0; i < askCount; i++)
                    state.Book.Upsert(Side.Ask, _asks[i].Price, _asks[i].Quantity);

                state.Generation = _generation;

                if (wasStale)
                    Interlocked.Increment(ref Statistics.Recoveries);

                return;
            }

            FeedProtocol.ReadIncremental(message, out _, out var instrumentId, out var side, out var level);
            var target = StateFor(instrumentId);

            if (target.Generation != _generation)
            {
                Interlocked.Increment(ref Statistics.IgnoredIncrementals);
                return;
            }

            if (type == FeedMessageType.Remove)
                target.Book.Remove(side, level.Price);
            else
                target.Book.Upsert(side, level.Price, level.Quantity);
        }

        private BookState StateFor(int instrumentId)
        {
            if (_books.TryGetValue(instrumentId, out var state))
                return state;

            state = new BookState(_bookFactory(instrumentId));
            _books[instrumentId] = state;
            return state;
        }

        private void Remember(ReadOnlySpan<byte> packet, FeedHeader header)
        {
            if (_recent.Count == RecentPacketLimit)
                _recent.Remove(_recentOrder.Dequeue());

            _recent[header.FirstSequence] = new PacketIdentity(header.MessageCount,
                header.PacketLength, BinaryPrimitives.ReadUInt32LittleEndian(
                    packet.Slice(FeedProtocol.ChecksumOffset, sizeof(uint))));
            _recentOrder.Enqueue(header.FirstSequence);
        }

        private readonly record struct PacketIdentity(ushort MessageCount, ushort PacketLength,
            uint Checksum)
        {
            public bool Matches(ReadOnlySpan<byte> packet, FeedHeader header)
                => header.MessageCount == MessageCount && header.PacketLength == PacketLength &&
                    BinaryPrimitives.ReadUInt32LittleEndian(
                        packet.Slice(FeedProtocol.ChecksumOffset, sizeof(uint))) == Checksum;
        }

        private sealed class BookState
        {
            public BookState(IOrderBook book) => Book = book;
            public IOrderBook Book { get; }
            public ulong Generation { get; set; }
        }

        private readonly object _gate = new object();
        private readonly Func<int, IOrderBook> _bookFactory;
        private readonly Dictionary<int, BookState> _books = new Dictionary<int, BookState>();
        private readonly Dictionary<ulong, byte[]> _held = new Dictionary<ulong, byte[]>();
        private readonly Dictionary<ulong, PacketIdentity> _recent =
            new Dictionary<ulong, PacketIdentity>(RecentPacketLimit);
        private readonly Queue<ulong> _recentOrder = new Queue<ulong>(RecentPacketLimit);
        private readonly List<ulong> _overtaken = new List<ulong>();
        private readonly HashSet<ulong> _retiredSessions = new HashSet<ulong>();
        private readonly Queue<ulong> _retiredSessionOrder = new Queue<ulong>();
        private readonly PriceLevel[] _bids = new PriceLevel[FeedProtocol.MaxSnapshotLevels];
        private readonly PriceLevel[] _asks = new PriceLevel[FeedProtocol.MaxSnapshotLevels];
        private ulong _expected;
        private ulong _sessionId;
        private ulong _generation;
        private bool _started;
        private const int RecentPacketLimit = 128;
    }
}
