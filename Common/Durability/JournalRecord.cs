using System;
using System.Buffers.Binary;
using MarketData.Common.Feed;

namespace MarketData.Common.Durability
{
    public enum JournalRecordType : byte
    {
        Invalid = 0,
        Message = 1,
        Checkpoint = 2,
        SegmentHeader = 3,
        Audit = 4,
        FeedPacket = 5,
    }

    /// <summary>CRC-32C-framed append record with a repeated trailing payload length.</summary>
    public static class JournalRecord
    {
        /// <summary>Record magic.</summary>
        public const uint Magic = 0x4A524E31; // "JRN1"

        public const int HeaderSize = 32;
        public const int TrailerSize = 4;
        public const int OverheadSize = HeaderSize + TrailerSize;

        public const int MaxPayloadSize = 1 << 20;

        // magic:4 type:1 reserved:3 length:4 sequence:8 timestamp:8 crc:4 = 32
        private const int TypeOffset = 4;
        private const int LengthOffset = 8;
        private const int SequenceOffset = 12;
        private const int TimestampOffset = 20;
        private const int CrcOffset = 28;

        public static int SizeFor(int payloadLength)
        {
            if ((uint)payloadLength > MaxPayloadSize)
                throw new ArgumentOutOfRangeException(nameof(payloadLength));

            return OverheadSize + payloadLength;
        }

        /// <summary>Writes a complete record into <paramref name="destination"/>.</summary>
        /// <returns>Bytes written.</returns>
        public static int Write(Span<byte> destination, JournalRecordType type, ulong sequence,
            long timestamp, ReadOnlySpan<byte> payload)
        {
            if (type is <= JournalRecordType.Invalid or > JournalRecordType.FeedPacket)
                throw new ArgumentOutOfRangeException(nameof(type), type, "Records need a real type.");

            if (payload.Length > MaxPayloadSize)
                throw new ArgumentOutOfRangeException(nameof(payload), payload.Length,
                    $"Payload exceeds {MaxPayloadSize} bytes.");

            var total = SizeFor(payload.Length);

            if (destination.Length < total)
                throw new ArgumentException("Destination too small for the record.", nameof(destination));

            BinaryPrimitives.WriteUInt32LittleEndian(destination, Magic);
            destination[TypeOffset] = (byte)type;
            destination[TypeOffset + 1] = 0;
            destination[TypeOffset + 2] = 0;
            destination[TypeOffset + 3] = 0;
            BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(LengthOffset, 4), payload.Length);
            BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(SequenceOffset, 8), sequence);
            BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(TimestampOffset, 8), timestamp);

            payload.CopyTo(destination.Slice(HeaderSize, payload.Length));

            // Everything but the CRC field itself, plus the payload.
            var crc = Crc32C.Compute(destination.Slice(0, CrcOffset), destination.Slice(HeaderSize, payload.Length));
            BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(CrcOffset, 4), crc);

            BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(HeaderSize + payload.Length, 4),
                payload.Length);

            return total;
        }

        /// <summary>Validates one record without copying; a short buffer is <c>Incomplete</c>.</summary>
        public static JournalReadResult TryRead(ReadOnlySpan<byte> source, out JournalRecordView record)
        {
            record = default;

            var headerResult = TryGetSize(source, out var total);

            if (headerResult != JournalReadResult.Ok)
                return headerResult;
            if (source.Length < total)
                return JournalReadResult.Incomplete;

            var length = total - OverheadSize;
            var stored = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(CrcOffset, 4));
            var actual = Crc32C.Compute(source.Slice(0, CrcOffset), source.Slice(HeaderSize, length));

            if (stored != actual)
                return JournalReadResult.BadChecksum;

            if (BinaryPrimitives.ReadInt32LittleEndian(source.Slice(HeaderSize + length, 4)) != length)
                return JournalReadResult.BadTrailer;

            record = new JournalRecordView(
                (JournalRecordType)source[TypeOffset],
                BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(SequenceOffset, 8)),
                BinaryPrimitives.ReadInt64LittleEndian(source.Slice(TimestampOffset, 8)),
                total,
                source.Slice(HeaderSize, length));

            return JournalReadResult.Ok;
        }

        internal static JournalReadResult TryGetSize(ReadOnlySpan<byte> source, out int total)
        {
            total = 0;

            if (source.Length < HeaderSize)
                return JournalReadResult.Incomplete;

            if (BinaryPrimitives.ReadUInt32LittleEndian(source) != Magic)
                return JournalReadResult.BadMagic;

            var type = (JournalRecordType)source[TypeOffset];
            if (type is <= JournalRecordType.Invalid or > JournalRecordType.FeedPacket)
                return JournalReadResult.BadType;

            if ((source[TypeOffset + 1] | source[TypeOffset + 2] | source[TypeOffset + 3]) != 0)
                return JournalReadResult.BadFlags;

            var length = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(LengthOffset, 4));
            if (length < 0 || length > MaxPayloadSize)
                return JournalReadResult.BadLength;

            total = SizeFor(length);
            return JournalReadResult.Ok;
        }
    }

    public enum JournalReadResult : byte
    {
        Ok = 0,

        /// <summary>The buffer ends inside the record. Recoverable: truncate here.</summary>
        Incomplete,

        BadMagic,
        BadType,
        BadFlags,
        BadLength,
        BadChecksum,
        BadTrailer,
        BadSession,
        SequenceGap,
        SegmentOrder,
    }

    /// <summary>A borrowed view over one record. Valid only while the source buffer is.</summary>
    public readonly ref struct JournalRecordView
    {
        public JournalRecordView(JournalRecordType type, ulong sequence, long timestamp,
            int totalSize, ReadOnlySpan<byte> payload)
        {
            Type = type;
            Sequence = sequence;
            Timestamp = timestamp;
            TotalSize = totalSize;
            Payload = payload;
        }

        public JournalRecordType Type { get; }
        public ulong Sequence { get; }
        public long Timestamp { get; }

        public int TotalSize { get; }

        public ReadOnlySpan<byte> Payload { get; }
    }
}
