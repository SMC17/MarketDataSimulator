using System;
using System.Buffers.Binary;
using MarketData.Common.Feed;

namespace MarketData.Common.Durability
{
    /// <summary>What a journal record describes.</summary>
    public enum JournalRecordType : byte
    {
        Invalid = 0,

        /// <summary>A sequenced market data message.</summary>
        Message = 1,

        /// <summary>A checkpoint marker: state up to this sequence is captured elsewhere.</summary>
        Checkpoint = 2,

        /// <summary>Opens a segment; carries the session and the sequence the segment starts at.</summary>
        SegmentHeader = 3,

        /// <summary>An auditable non-market event (risk decision, kill switch, entitlement change).</summary>
        Audit = 4,
    }

    /// <summary>
    /// Fixed-layout framing for one journal record.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The layout exists to make a <em>partial</em> record detectable, which is the only thing that
    /// matters after a crash. A process killed mid-append leaves a prefix of a record on disk, and
    /// a reader that cannot tell that prefix from a complete record will either resurrect a message
    /// that was never durable or silently truncate the log at the wrong place.
    /// </para>
    /// <para>
    /// Three properties give that detection. The length precedes the payload, so a reader knows how
    /// much to expect before it reads it. The CRC covers header and payload together, so a torn
    /// write that happens to leave a plausible length still fails. And the trailing length repeats
    /// the leading one, so the log can also be walked backwards from its tail - which is how
    /// recovery finds the last complete record without scanning from the beginning.
    /// </para>
    /// <para>
    /// CRC-32C rather than a hash: it is a corruption check, not a signature, and the hardware
    /// instruction makes it cost roughly nothing next to the write itself.
    /// </para>
    /// </remarks>
    public static class JournalRecord
    {
        /// <summary>Marks the start of a record. Also the resynchronisation point after damage.</summary>
        public const uint Magic = 0x4A524E31; // "JRN1"

        public const int HeaderSize = 32;
        public const int TrailerSize = 4;
        public const int OverheadSize = HeaderSize + TrailerSize;

        /// <summary>Largest payload a single record may carry.</summary>
        public const int MaxPayloadSize = 1 << 20;

        // magic:4 type:1 reserved:3 length:4 sequence:8 timestamp:8 crc:4 = 32
        private const int TypeOffset = 4;
        private const int LengthOffset = 8;
        private const int SequenceOffset = 12;
        private const int TimestampOffset = 20;
        private const int CrcOffset = 28;

        public static int SizeFor(int payloadLength) => OverheadSize + payloadLength;

        /// <summary>Writes a complete record into <paramref name="destination"/>.</summary>
        /// <returns>Bytes written.</returns>
        public static int Write(Span<byte> destination, JournalRecordType type, ulong sequence,
            long timestamp, ReadOnlySpan<byte> payload)
        {
            if (type == JournalRecordType.Invalid)
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

            // Trailing length so the log can be walked backwards from the tail.
            BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(HeaderSize + payload.Length, 4),
                payload.Length);

            return total;
        }

        /// <summary>
        /// Validates a record at the start of <paramref name="source"/> without copying it.
        /// </summary>
        /// <remarks>
        /// Returns <see cref="JournalReadResult.Incomplete"/> rather than an error when the buffer
        /// simply ends early. The distinction is the whole point: an incomplete tail is the normal
        /// state of a log whose writer was killed, and is recoverable by truncation, whereas a
        /// checksum failure in the middle of a log is corruption and is not.
        /// </remarks>
        public static JournalReadResult TryRead(ReadOnlySpan<byte> source, out JournalRecordView record)
        {
            record = default;

            if (source.Length < HeaderSize)
                return JournalReadResult.Incomplete;

            if (BinaryPrimitives.ReadUInt32LittleEndian(source) != Magic)
                return JournalReadResult.BadMagic;

            var type = (JournalRecordType)source[TypeOffset];
            if (type == JournalRecordType.Invalid || type > JournalRecordType.Audit)
                return JournalReadResult.BadType;

            var length = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(LengthOffset, 4));
            if (length < 0 || length > MaxPayloadSize)
                return JournalReadResult.BadLength;

            var total = SizeFor(length);
            if (source.Length < total)
                return JournalReadResult.Incomplete;

            var stored = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(CrcOffset, 4));
            var actual = Crc32C.Compute(source.Slice(0, CrcOffset), source.Slice(HeaderSize, length));

            if (stored != actual)
                return JournalReadResult.BadChecksum;

            if (BinaryPrimitives.ReadInt32LittleEndian(source.Slice(HeaderSize + length, 4)) != length)
                return JournalReadResult.BadTrailer;

            record = new JournalRecordView(
                type,
                BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(SequenceOffset, 8)),
                BinaryPrimitives.ReadInt64LittleEndian(source.Slice(TimestampOffset, 8)),
                total,
                source.Slice(HeaderSize, length));

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
        BadLength,
        BadChecksum,
        BadTrailer,
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

        /// <summary>Bytes this record occupies, header and trailer included.</summary>
        public int TotalSize { get; }

        public ReadOnlySpan<byte> Payload { get; }
    }
}
