using System.Buffers.Binary;
using MarketData.Common.Books;

namespace MarketData.Common.Feed
{
    public enum FeedMessageType : byte
    {
        Invalid = 0,
        Add = 1,
        Replace = 2,
        Remove = 3,
        Snapshot = 4,
        Heartbeat = 5,
    }

    public enum FeedProtocolError : byte
    {
        None = 0,
        Truncated,
        Magic,
        Version,
        Flags,
        MessageCount,
        PacketLength,
        SequenceOverflow,
        Checksum,
    }

    public readonly record struct FeedHeader(
        ushort MessageCount,
        ushort PacketLength,
        ulong SessionId,
        ulong FirstSequence,
        long SourceTimestamp);

    /// <summary>Versioned, sequenced, integrity-checked multicast wire format.</summary>
    public static class FeedProtocol
    {
        public const byte Magic = 0x4D;
        public const byte Version = 2;
        public const int HeaderSize = 36;
        public const int IncrementalSize = 14;
        public const int MaxPacketSize = 1400;
        public const int MaxSnapshotLevels = (MaxPacketSize - HeaderSize - 7) / 8;

        public const int ChecksumOffset = 32;

        /// <summary>Completes a packet after its payload has been encoded.</summary>
        public static void WriteHeader(Span<byte> packet, ushort messageCount, ulong sessionId,
            ulong firstSequence, long sourceTimestamp)
        {
            if (packet.Length < HeaderSize || packet.Length > MaxPacketSize)
                throw new ArgumentOutOfRangeException(nameof(packet));
            if (messageCount == 0)
                throw new ArgumentOutOfRangeException(nameof(messageCount));
            if (sessionId == 0)
                throw new ArgumentOutOfRangeException(nameof(sessionId));
            if (firstSequence > ulong.MaxValue - messageCount)
                throw new ArgumentOutOfRangeException(nameof(firstSequence));

            packet[0] = Magic;
            packet[1] = Version;
            BinaryPrimitives.WriteUInt16LittleEndian(packet.Slice(2, 2), 0);
            BinaryPrimitives.WriteUInt16LittleEndian(packet.Slice(4, 2), messageCount);
            BinaryPrimitives.WriteUInt16LittleEndian(packet.Slice(6, 2), checked((ushort)packet.Length));
            BinaryPrimitives.WriteUInt64LittleEndian(packet.Slice(8, 8), sessionId);
            BinaryPrimitives.WriteUInt64LittleEndian(packet.Slice(16, 8), firstSequence);
            BinaryPrimitives.WriteInt64LittleEndian(packet.Slice(24, 8), sourceTimestamp);
            BinaryPrimitives.WriteUInt32LittleEndian(packet.Slice(ChecksumOffset, 4),
                Crc32C.Compute(packet.Slice(0, ChecksumOffset), packet.Slice(HeaderSize)));
        }

        public static bool TryReadHeader(ReadOnlySpan<byte> packet, out FeedHeader header,
            out FeedProtocolError error)
        {
            header = default;

            if (packet.Length < HeaderSize)
                return Fail(FeedProtocolError.Truncated, out error);
            if (packet.Length > MaxPacketSize)
                return Fail(FeedProtocolError.PacketLength, out error);
            if (packet[0] != Magic)
                return Fail(FeedProtocolError.Magic, out error);
            if (packet[1] != Version)
                return Fail(FeedProtocolError.Version, out error);
            if (BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(2, 2)) != 0)
                return Fail(FeedProtocolError.Flags, out error);

            var count = BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(4, 2));
            var declaredLength = BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(6, 2));
            var session = BinaryPrimitives.ReadUInt64LittleEndian(packet.Slice(8, 8));
            var sequence = BinaryPrimitives.ReadUInt64LittleEndian(packet.Slice(16, 8));

            if (count == 0 || session == 0)
                return Fail(FeedProtocolError.MessageCount, out error);
            if (declaredLength != packet.Length)
                return Fail(FeedProtocolError.PacketLength, out error);
            if (sequence > ulong.MaxValue - count)
                return Fail(FeedProtocolError.SequenceOverflow, out error);

            var expectedChecksum = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(ChecksumOffset, 4));
            var actualChecksum = Crc32C.Compute(packet.Slice(0, ChecksumOffset), packet.Slice(HeaderSize));

            if (actualChecksum != expectedChecksum)
                return Fail(FeedProtocolError.Checksum, out error);

            header = new FeedHeader(count, declaredLength, session, sequence,
                BinaryPrimitives.ReadInt64LittleEndian(packet.Slice(24, 8)));
            error = FeedProtocolError.None;
            return true;
        }

        private static bool Fail(FeedProtocolError value, out FeedProtocolError error)
        {
            error = value;
            return false;
        }

        public static int WriteIncremental(Span<byte> buffer, FeedMessageType type, int instrumentId,
            Side side, PriceLevel level)
        {
            if (buffer.Length < IncrementalSize)
                throw new ArgumentException("destination is too small", nameof(buffer));
            if (type is not (FeedMessageType.Add or FeedMessageType.Replace or FeedMessageType.Remove))
                throw new ArgumentOutOfRangeException(nameof(type));
            if (side is not (Side.Bid or Side.Ask))
                throw new ArgumentOutOfRangeException(nameof(side));

            buffer[0] = (byte)type;
            BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(1, 4), instrumentId);
            BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(5, 4), level.Price);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(9, 4), level.Quantity);
            buffer[13] = (byte)side;
            return IncrementalSize;
        }

        public static int ReadIncremental(ReadOnlySpan<byte> buffer, out FeedMessageType type,
            out int instrumentId, out Side side, out PriceLevel level)
        {
            type = (FeedMessageType)buffer[0];
            instrumentId = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(1, 4));
            var price = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(5, 4));
            var quantity = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(9, 4));
            side = (Side)buffer[13];
            level = new PriceLevel(price, quantity);
            return IncrementalSize;
        }

        public static int SnapshotSize(int bidCount, int askCount)
        {
            ValidateSnapshotCounts(bidCount, askCount);
            return 7 + checked((bidCount + askCount) * 8);
        }

        public static int WriteSnapshot(Span<byte> buffer, int instrumentId,
            ReadOnlySpan<PriceLevel> bids, ReadOnlySpan<PriceLevel> asks)
        {
            var size = SnapshotSize(bids.Length, asks.Length);

            if (buffer.Length < size)
                throw new ArgumentException("destination is too small", nameof(buffer));

            buffer[0] = (byte)FeedMessageType.Snapshot;
            BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(1, 4), instrumentId);
            buffer[5] = (byte)bids.Length;
            buffer[6] = (byte)asks.Length;

            var offset = 7;
            offset += WriteLevels(buffer.Slice(offset), bids);
            offset += WriteLevels(buffer.Slice(offset), asks);
            return offset;
        }

        private static void ValidateSnapshotCounts(int bidCount, int askCount)
        {
            if ((uint)bidCount > byte.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(bidCount));
            if ((uint)askCount > byte.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(askCount));
            if (bidCount + askCount > MaxSnapshotLevels)
                throw new ArgumentOutOfRangeException(nameof(bidCount),
                    $"a snapshot may contain at most {MaxSnapshotLevels} total levels");
        }

        private static int WriteLevels(Span<byte> buffer, ReadOnlySpan<PriceLevel> levels)
        {
            var offset = 0;

            foreach (var level in levels)
            {
                BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(offset, 4), level.Price);
                BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(offset + 4, 4), level.Quantity);
                offset += 8;
            }

            return offset;
        }

        public static int ReadSnapshot(ReadOnlySpan<byte> buffer, out int instrumentId,
            Span<PriceLevel> bids, out int bidCount, Span<PriceLevel> asks, out int askCount)
        {
            instrumentId = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(1, 4));
            bidCount = buffer[5];
            askCount = buffer[6];

            var offset = 7;
            offset += ReadLevels(buffer.Slice(offset), bids, bidCount);
            offset += ReadLevels(buffer.Slice(offset), asks, askCount);
            return offset;
        }

        private static int ReadLevels(ReadOnlySpan<byte> buffer, Span<PriceLevel> levels, int count)
        {
            var offset = 0;

            for (var i = 0; i < count; i++)
            {
                var price = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(offset, 4));
                var quantity = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(offset + 4, 4));

                if (i < levels.Length)
                    levels[i] = new PriceLevel(price, quantity);

                offset += 8;
            }

            return offset;
        }

        /// <summary>Length of one valid message, or -1 when it is malformed.</summary>
        public static int MessageLength(ReadOnlySpan<byte> buffer)
        {
            if (buffer.Length < 1)
                return -1;

            switch ((FeedMessageType)buffer[0])
            {
                case FeedMessageType.Add:
                case FeedMessageType.Replace:
                case FeedMessageType.Remove:
                    if (buffer.Length < IncrementalSize || buffer[13] > (byte)Side.Ask)
                        return -1;
                    return IncrementalSize;

                case FeedMessageType.Heartbeat:
                    return 1;

                case FeedMessageType.Snapshot:
                    if (buffer.Length < 7)
                        return -1;

                    var total = buffer[5] + buffer[6];
                    if (total > MaxSnapshotLevels)
                        return -1;

                    return 7 + total * 8;

                default:
                    return -1;
            }
        }
    }
}
