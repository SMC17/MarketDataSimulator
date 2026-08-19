using System;
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

    /// <summary>
    /// Wire format for the multicast feed: a sequenced packet header followed by one or more
    /// messages.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Hand-rolled and fixed-layout rather than a general-purpose serialiser. Every field is at a
    /// known offset, so encoding is a handful of stores into a caller-supplied span and decoding
    /// is a handful of loads - no reflection, no schema walk, no allocation on either side. On a
    /// path that runs once per update per packet, that matters more than flexibility.
    /// </para>
    /// <para>
    /// Explicitly little-endian via <see cref="BinaryPrimitives"/> rather than whatever the host
    /// happens to be. A wire format that silently depends on the sender's byte order works
    /// perfectly until the day it does not.
    /// </para>
    /// <para>
    /// Packets are capped below the Ethernet MTU. An IP-fragmented datagram is lost in its
    /// entirety if any single fragment is dropped, which converts a small loss probability into a
    /// larger one for no benefit; batching stops short of the fragmentation threshold instead.
    /// </para>
    /// </remarks>
    public static class FeedProtocol
    {
        public const byte Magic = 0x4D;
        public const byte Version = 1;

        /// <summary>Magic, version, message count, first sequence, source timestamp.</summary>
        public const int HeaderSize = 20;

        /// <summary>Type, instrument, price, quantity, side.</summary>
        public const int IncrementalSize = 14;

        /// <summary>
        /// Chosen to sit under a 1500-byte Ethernet MTU once IP and UDP headers are accounted for,
        /// so a packet is never fragmented.
        /// </summary>
        public const int MaxPacketSize = 1400;

        public static void WriteHeader(Span<byte> buffer, ushort messageCount, ulong firstSequence, long sourceTimestamp)
        {
            buffer[0] = Magic;
            buffer[1] = Version;
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(2, 2), messageCount);
            BinaryPrimitives.WriteUInt64LittleEndian(buffer.Slice(4, 8), firstSequence);
            BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(12, 8), sourceTimestamp);
        }

        public static bool TryReadHeader(ReadOnlySpan<byte> buffer,
            out ushort messageCount, out ulong firstSequence, out long sourceTimestamp)
        {
            messageCount = 0;
            firstSequence = 0;
            sourceTimestamp = 0;

            if (buffer.Length < HeaderSize || buffer[0] != Magic || buffer[1] != Version)
                return false;

            messageCount = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(2, 2));
            firstSequence = BinaryPrimitives.ReadUInt64LittleEndian(buffer.Slice(4, 8));
            sourceTimestamp = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(12, 8));
            return true;
        }

        public static int WriteIncremental(Span<byte> buffer, FeedMessageType type, int instrumentId, Side side, PriceLevel level)
        {
            buffer[0] = (byte)type;
            BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(1, 4), instrumentId);
            BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(5, 4), level.Price);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(9, 4), level.Quantity);
            buffer[13] = (byte)side;
            return IncrementalSize;
        }

        public static int ReadIncremental(ReadOnlySpan<byte> buffer,
            out FeedMessageType type, out int instrumentId, out Side side, out PriceLevel level)
        {
            type = (FeedMessageType)buffer[0];
            instrumentId = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(1, 4));
            var price = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(5, 4));
            var quantity = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(9, 4));
            side = (Side)buffer[13];
            level = new PriceLevel(price, quantity);
            return IncrementalSize;
        }

        public static int SnapshotSize(int bidCount, int askCount) => 7 + (bidCount + askCount) * 8;

        public static int WriteSnapshot(Span<byte> buffer, int instrumentId,
            ReadOnlySpan<PriceLevel> bids, ReadOnlySpan<PriceLevel> asks)
        {
            buffer[0] = (byte)FeedMessageType.Snapshot;
            BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(1, 4), instrumentId);
            buffer[5] = (byte)bids.Length;
            buffer[6] = (byte)asks.Length;

            var offset = 7;
            offset += WriteLevels(buffer.Slice(offset), bids);
            offset += WriteLevels(buffer.Slice(offset), asks);
            return offset;
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

        /// <summary>Length of the message beginning at <paramref name="buffer"/>, or -1 if malformed.</summary>
        public static int MessageLength(ReadOnlySpan<byte> buffer)
        {
            if (buffer.Length < 1)
                return -1;

            return (FeedMessageType)buffer[0] switch
            {
                FeedMessageType.Add or FeedMessageType.Replace or FeedMessageType.Remove => IncrementalSize,
                FeedMessageType.Heartbeat => 1,
                FeedMessageType.Snapshot when buffer.Length >= 7 => SnapshotSize(buffer[5], buffer[6]),
                _ => -1,
            };
        }
    }
}
