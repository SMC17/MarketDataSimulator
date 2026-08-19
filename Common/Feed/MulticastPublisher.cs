using MarketData.Common.Books;
using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using MarketData.Common.Durability;

namespace MarketData.Common.Feed
{
    /// <summary>Publishes sealed, journal-first feed packets over one or two multicast lines.</summary>
    public sealed class MulticastPublisher : IDisposable
    {
        public ulong Sequence { get { lock (_lock) return _sequence; } }
        public ulong SessionId { get; }
        public long PacketsSent => Interlocked.Read(ref _packetsSent);
        public long MessagesSent => Interlocked.Read(ref _messagesSent);
        public long BytesSent => Interlocked.Read(ref _bytesSent);
        public long SendFailures => Interlocked.Read(ref _sendFailures);
        public long JournalFailures => Interlocked.Read(ref _journalFailures);

        /// <param name="redundantGroup">Optional B line carrying the identical sealed packet.</param>
        public MulticastPublisher(IPAddress group, int port, IPAddress @interface = null, int maxBatch = 64,
            IPAddress redundantGroup = null, int redundantPort = 0, ulong sessionId = 0,
            WriteAheadJournal journal = null)
        {
            ArgumentNullException.ThrowIfNull(group);
            if ((uint)(port - 1) >= 65535)
                throw new ArgumentOutOfRangeException(nameof(port));
            if (maxBatch is < 1 or > ushort.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(maxBatch));

            _endpoint = new IPEndPoint(group, port);
            _redundantEndpoint = redundantGroup is null
                ? null
                : new IPEndPoint(redundantGroup, redundantPort > 0 ? redundantPort : port);
            _maxBatch = maxBatch;
            SessionId = sessionId == 0 ? NewSessionId() : sessionId;

            if (journal is not null && journal.SessionId != SessionId)
                throw new ArgumentException("Publisher and journal sessions differ.", nameof(journal));

            _journal = journal;
            _sequence = journal?.NextSequence ?? 0;

            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);

            // Loopback delivery must be on for a publisher and its subscribers to share a host.
            _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);

            if (@interface is not null)
            {
                _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface,
                    @interface.GetAddressBytes());
            }
        }

        public static ulong NewSessionId()
        {
            Span<byte> bytes = stackalloc byte[sizeof(ulong)];

            do
            {
                RandomNumberGenerator.Fill(bytes);
            }
            while (BinaryPrimitives.ReadUInt64LittleEndian(bytes) == 0);

            return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        }

        /// <summary>Appends an incremental to the current packet, flushing first if it will not fit.</summary>
        public void Publish(FeedMessageType type, int instrumentId, Side side, PriceLevel level)
        {
            lock (_lock)
            {
                ThrowIfFaulted();

                if (_pending == _maxBatch || _offset + FeedProtocol.IncrementalSize > FeedProtocol.MaxPacketSize)
                    FlushLocked();

                _offset += FeedProtocol.WriteIncremental(_buffer.AsSpan(_offset), type, instrumentId, side, level);
                _pending++;
            }
        }

        public void PublishSnapshot(int instrumentId, ReadOnlySpan<PriceLevel> bids, ReadOnlySpan<PriceLevel> asks)
        {
            var size = FeedProtocol.SnapshotSize(bids.Length, asks.Length);

            lock (_lock)
            {
                ThrowIfFaulted();

                if (_pending == _maxBatch || _offset + size > FeedProtocol.MaxPacketSize)
                    FlushLocked();

                _offset += FeedProtocol.WriteSnapshot(_buffer.AsSpan(_offset), instrumentId, bids, asks);
                _pending++;
            }
        }

        public void Flush()
        {
            lock (_lock)
            {
                ThrowIfFaulted();
                FlushLocked();
            }
        }

        private void FlushLocked()
        {
            if (_pending == 0)
                return;

            // Timestamp the sealed packet at the publication boundary.
            FeedProtocol.WriteHeader(_buffer.AsSpan(0, _offset), (ushort)_pending, SessionId,
                _sequence, Stopwatch.GetTimestamp());

            if (_journal is not null)
            {
                try
                {
                    _journal.AppendPacket(_buffer.AsSpan(0, _offset));
                }
                catch
                {
                    _faulted = true;
                    Interlocked.Increment(ref _journalFailures);
                    throw;
                }
            }

            var successfulSends = 0;

            try
            {
                if (TrySend(_endpoint))
                    successfulSends++;

                if (_redundantEndpoint is not null && TrySend(_redundantEndpoint))
                    successfulSends++;
            }
            finally
            {
                // A journalled or partially sent packet cannot reuse its sequence.
                _sequence = checked(_sequence + (uint)_pending);
                Interlocked.Add(ref _packetsSent, successfulSends);
                Interlocked.Add(ref _bytesSent, (long)_offset * successfulSends);

                if (successfulSends > 0)
                    Interlocked.Add(ref _messagesSent, _pending);

                _pending = 0;
                _offset = FeedProtocol.HeaderSize;
            }
        }

        private bool TrySend(EndPoint endpoint)
        {
            try
            {
                _socket.SendTo(_buffer, 0, _offset, SocketFlags.None, endpoint);
                return true;
            }
            catch (SocketException)
            {
                Interlocked.Increment(ref _sendFailures);
                return false;
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                try
                {
                    if (!_faulted)
                        FlushLocked();
                }
                finally
                {
                    _socket.Dispose();
                }
            }
        }

        private void ThrowIfFaulted()
        {
            if (_faulted)
                throw new InvalidOperationException("Publisher is fail-stopped after a journal error.");
        }

        private readonly IPEndPoint _endpoint;
        private readonly IPEndPoint _redundantEndpoint;
        private readonly Socket _socket;
        private readonly WriteAheadJournal _journal;
        private readonly int _maxBatch;
        private readonly object _lock = new object();
        private readonly byte[] _buffer = new byte[FeedProtocol.MaxPacketSize];
        private int _offset = FeedProtocol.HeaderSize;
        private int _pending;
        private ulong _sequence;
        private long _packetsSent;
        private long _messagesSent;
        private long _bytesSent;
        private long _sendFailures;
        private long _journalFailures;
        private bool _faulted;
    }
}
