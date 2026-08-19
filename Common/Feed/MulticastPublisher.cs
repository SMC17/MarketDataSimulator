using MarketData.Common.Books;
using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;

namespace MarketData.Common.Feed
{
    /// <summary>
    /// Publishes the feed as sequenced multicast datagrams.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The point of the whole exercise: publishing costs one <c>send</c> regardless of how many
    /// subscribers are listening. The unicast path this replaces performs one write per
    /// subscriber per update, so its cost - and the latency spread across the subscriber
    /// population - grows linearly with the audience. Here the network performs the replication,
    /// and the server does not know or care how many receivers exist.
    /// </para>
    /// <para>
    /// That property is bought with reliability. UDP multicast has no retransmission and no
    /// backpressure: a subscriber that cannot keep up simply loses packets, and the publisher
    /// never finds out. What makes this workable is the sequence number on every packet, which
    /// turns silent loss into detectable loss, plus a periodic snapshot that gives a subscriber
    /// which has detected a gap a way back to a known-good state.
    /// </para>
    /// <para>
    /// Messages are batched into a packet up to the fragmentation threshold, which amortises the
    /// per-datagram cost - syscall, IP and UDP headers - over many updates. Batching trades a
    /// little latency for a lot of throughput, so the batch is also flushed on a deadline rather
    /// than only when full.
    /// </para>
    /// </remarks>
    public sealed class MulticastPublisher : IDisposable
    {
        public ulong Sequence => (ulong)Interlocked.Read(ref _sequence);
        public ulong SessionId { get; }
        public long PacketsSent => Interlocked.Read(ref _packetsSent);
        public long MessagesSent => Interlocked.Read(ref _messagesSent);
        public long BytesSent => Interlocked.Read(ref _bytesSent);
        public long SendFailures => Interlocked.Read(ref _sendFailures);

        /// <param name="redundantGroup">
        /// Optional second group carrying an identical copy of the feed. Real exchanges publish an
        /// A and a B line over disjoint paths so that a drop on one is covered by the other;
        /// subscribers take whichever copy arrives first. It costs one extra send per packet -
        /// still independent of the subscriber count - and roughly squares the probability that a
        /// given packet is lost to every subscriber.
        /// </param>
        public MulticastPublisher(IPAddress group, int port, IPAddress @interface = null, int maxBatch = 64,
            IPAddress redundantGroup = null, int redundantPort = 0, ulong sessionId = 0)
        {
            ArgumentNullException.ThrowIfNull(group);
            if ((uint)(port - 1) >= 65535)
                throw new ArgumentOutOfRangeException(nameof(port));

            _endpoint = new IPEndPoint(group, port);
            _redundantEndpoint = redundantGroup is null
                ? null
                : new IPEndPoint(redundantGroup, redundantPort > 0 ? redundantPort : port);
            _maxBatch = Math.Max(1, maxBatch);
            SessionId = sessionId == 0 ? CreateSessionId() : sessionId;

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

        private static ulong CreateSessionId()
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
                if (_pending == _maxBatch || _offset + size > FeedProtocol.MaxPacketSize)
                    FlushLocked();

                _offset += FeedProtocol.WriteSnapshot(_buffer.AsSpan(_offset), instrumentId, bids, asks);
                _pending++;
            }
        }

        public void Flush()
        {
            lock (_lock)
                FlushLocked();
        }

        private void FlushLocked()
        {
            if (_pending == 0)
                return;

            // Stamped at the moment of transmission rather than of generation, because everything
            // before this point is measured separately and a subscriber can only observe from here.
            FeedProtocol.WriteHeader(_buffer.AsSpan(0, _offset), (ushort)_pending, SessionId,
                (ulong)_sequence, Stopwatch.GetTimestamp());

            var successfulSends = TrySend(_endpoint) ? 1 : 0;

            if (_redundantEndpoint is not null && TrySend(_redundantEndpoint))
                successfulSends++;

            // Sequence advances even when every send fails so downstream loss is detectable.
            Interlocked.Add(ref _sequence, _pending);
            Interlocked.Add(ref _packetsSent, successfulSends);
            Interlocked.Add(ref _bytesSent, (long)_offset * successfulSends);

            if (successfulSends > 0)
                Interlocked.Add(ref _messagesSent, _pending);

            _pending = 0;
            _offset = FeedProtocol.HeaderSize;
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
                FlushLocked();
                _socket.Dispose();
            }
        }

        private readonly IPEndPoint _endpoint;
        private readonly IPEndPoint _redundantEndpoint;
        private readonly Socket _socket;
        private readonly int _maxBatch;
        private readonly object _lock = new object();
        private readonly byte[] _buffer = new byte[FeedProtocol.MaxPacketSize];
        private int _offset = FeedProtocol.HeaderSize;
        private int _pending;
        private long _sequence;
        private long _packetsSent;
        private long _messagesSent;
        private long _bytesSent;
        private long _sendFailures;
    }
}
