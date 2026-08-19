using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MarketData.Common.Books;
using MarketData.Common.Feed;

namespace MarketData.Common.Durability
{
    /// <summary>Versioned, checksummed full-depth state checkpoint.</summary>
    public static class Checkpoint
    {
        public const uint Magic = 0x43484B32; // CHK2
        public const ushort Version = 2;
        public const int HeaderSize = 40;
        public const int TrailerSize = 8;
        public const int MaxCheckpointBytes = 256 * 1024 * 1024;

        private const uint CommitMagic = 0xC04D17ED;
        private const string Prefix = "checkpoint-";
        private const string Suffix = ".chk";
        private const int MaxInstruments = 1_000_000;
        private const int CrcOffset = 36;

        public static string Write(string directory, WriteAheadJournal journal, ulong sequence,
            ulong sessionId, IReadOnlyDictionary<int, IOrderBook> books)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(directory);
            ArgumentNullException.ThrowIfNull(journal);
            ArgumentNullException.ThrowIfNull(books);
            if (sessionId == 0 || sessionId != journal.SessionId)
                throw new InvalidDataException("Checkpoint and journal sessions differ.");
            if (!journal.HasSequencedRecords || sequence > journal.LastSequence)
                throw new InvalidDataException("Checkpoint is outside the durable prefix.");
            if (books.Count > MaxInstruments)
                throw new ArgumentOutOfRangeException(nameof(books));

            var instrumentIds = books.Keys.ToArray();
            Array.Sort(instrumentIds);
            var payloadLength = PayloadLength(instrumentIds, books);
            var totalLength = checked(HeaderSize + payloadLength + TrailerSize);

            if (totalLength > MaxCheckpointBytes)
                throw new InvalidDataException("Checkpoint exceeds the configured format bound.");

            var bytes = GC.AllocateUninitializedArray<byte>(totalLength);
            WriteHeader(bytes, sessionId, sequence, instrumentIds.Length, payloadLength, totalLength);
            WritePayload(bytes.AsSpan(HeaderSize, payloadLength), instrumentIds, books);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(totalLength - TrailerSize), totalLength);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(totalLength - sizeof(uint)), CommitMagic);

            var crc = Crc32C.Compute(bytes.AsSpan(0, CrcOffset),
                bytes.AsSpan(HeaderSize, payloadLength));
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(CrcOffset), crc);

            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"{Prefix}{sequence:D20}{Suffix}");
            var temporary = path + ".tmp";

            try
            {
                using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write,
                           FileShare.None, bufferSize: 1, FileOptions.SequentialScan))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(temporary, path, overwrite: true);

                Span<byte> marker = stackalloc byte[sizeof(ulong)];
                BinaryPrimitives.WriteUInt64LittleEndian(marker, sequence);
                journal.Append(JournalRecordType.Checkpoint, sequence, DateTime.UtcNow.Ticks, marker);
                journal.Sync();
                return path;
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
        }

        public static string FindLatest(string directory, ulong notAfter = ulong.MaxValue)
        {
            if (!Directory.Exists(directory))
                return null;

            return Directory.GetFiles(directory, Prefix + "*" + Suffix)
                .Select(path => (path, valid: TryGetSequence(path, out var sequence), sequence))
                .Where(entry => entry.valid && entry.sequence <= notAfter)
                .OrderByDescending(entry => entry.sequence)
                .Select(entry => entry.path)
                .FirstOrDefault();
        }

        public static ulong SequenceOf(string path)
        {
            return TryGetSequence(path, out var sequence) ? sequence : Sequencer.None;
        }

        public static ulong Restore(string path, Func<int, IOrderBook> bookFactory,
            IDictionary<int, IOrderBook> books, ulong expectedSessionId = 0)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            ArgumentNullException.ThrowIfNull(bookFactory);
            ArgumentNullException.ThrowIfNull(books);

            var info = new FileInfo(path);
            if (info.Length < HeaderSize + TrailerSize || info.Length > MaxCheckpointBytes)
                throw new InvalidDataException("Checkpoint length is invalid.");

            var bytes = File.ReadAllBytes(path);
            var span = bytes.AsSpan();

            if (BinaryPrimitives.ReadUInt32LittleEndian(span) != Magic ||
                BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(4)) != Version ||
                BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(6)) != HeaderSize)
                throw new InvalidDataException("Checkpoint format is unsupported.");

            var sessionId = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(8));
            var sequence = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(16));
            var instrumentCount = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(24));
            var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(28));
            var totalLength = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(32));

            if (sessionId == 0 || (expectedSessionId != 0 && sessionId != expectedSessionId))
                throw new InvalidDataException("Checkpoint session does not match.");
            if (instrumentCount < 0 || instrumentCount > MaxInstruments || payloadLength < 0 ||
                totalLength != bytes.Length || totalLength != HeaderSize + payloadLength + TrailerSize)
                throw new InvalidDataException("Checkpoint framing is invalid.");
            if (BinaryPrimitives.ReadInt32LittleEndian(span.Slice(totalLength - TrailerSize)) !=
                    totalLength ||
                BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(totalLength - sizeof(uint))) !=
                    CommitMagic)
                throw new InvalidDataException("Checkpoint commit trailer is invalid.");

            var storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(CrcOffset));
            var actualCrc = Crc32C.Compute(span.Slice(0, CrcOffset),
                span.Slice(HeaderSize, payloadLength));
            if (storedCrc != actualCrc)
                throw new InvalidDataException("Checkpoint checksum failed.");

            var restored = new Dictionary<int, IOrderBook>(instrumentCount);
            var payload = span.Slice(HeaderSize, payloadLength);
            var offset = 0;

            for (var i = 0; i < instrumentCount; i++)
            {
                EnsureRemaining(payload, offset, 12);
                var instrumentId = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(offset));
                var bidCount = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(offset + 4));
                var askCount = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(offset + 8));
                offset += 12;

                var maxLevels = (payload.Length - offset) / 8;
                if (instrumentId <= 0 || bidCount < 0 || askCount < 0 ||
                    bidCount > maxLevels || askCount > maxLevels ||
                    restored.ContainsKey(instrumentId))
                    throw new InvalidDataException("Checkpoint instrument table is invalid.");

                var book = bookFactory(instrumentId);
                if (book is null || !restored.TryAdd(instrumentId, book))
                    throw new InvalidDataException("Checkpoint book factory failed.");

                if (bidCount > book.Depth || askCount > book.Depth)
                    throw new InvalidDataException("Checkpoint depth exceeds the target book.");

                offset = ReadSide(payload, offset, bidCount, Side.Bid, book);
                offset = ReadSide(payload, offset, askCount, Side.Ask, book);
            }

            if (offset != payload.Length)
                throw new InvalidDataException("Checkpoint payload has trailing data.");

            books.Clear();
            foreach (var entry in restored)
                books.Add(entry.Key, entry.Value);

            return sequence;
        }

        public static int Prune(string directory, int keep = 3)
        {
            if (keep < 1)
                throw new ArgumentOutOfRangeException(nameof(keep));
            if (!Directory.Exists(directory))
                return 0;

            var ordered = Directory.GetFiles(directory, Prefix + "*" + Suffix)
                .OrderByDescending(SequenceOf)
                .ToList();
            var removed = 0;

            foreach (var path in ordered.Skip(keep))
            {
                File.Delete(path);
                removed++;
            }

            return removed;
        }

        private static int PayloadLength(int[] instrumentIds,
            IReadOnlyDictionary<int, IOrderBook> books)
        {
            var length = 0;

            foreach (var instrumentId in instrumentIds)
            {
                var book = books[instrumentId] ??
                    throw new InvalidDataException("Checkpoint book cannot be null.");
                length = checked(length + 12 + checked((book.Count(Side.Bid) +
                    book.Count(Side.Ask)) * 8));
            }

            return length;
        }

        private static bool TryGetSequence(string path, out ulong sequence)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            sequence = Sequencer.None;
            return name.StartsWith(Prefix, StringComparison.Ordinal) &&
                ulong.TryParse(name.AsSpan(Prefix.Length), out sequence);
        }

        private static void WriteHeader(Span<byte> destination, ulong sessionId, ulong sequence,
            int instruments, int payloadLength, int totalLength)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(destination, Magic);
            BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(4), Version);
            BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(6), HeaderSize);
            BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(8), sessionId);
            BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(16), sequence);
            BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(24), instruments);
            BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(28), payloadLength);
            BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(32), totalLength);
            BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(CrcOffset), 0);
        }

        private static void WritePayload(Span<byte> destination, int[] instrumentIds,
            IReadOnlyDictionary<int, IOrderBook> books)
        {
            var offset = 0;

            foreach (var instrumentId in instrumentIds)
            {
                var book = books[instrumentId];
                var bids = new PriceLevel[book.Count(Side.Bid)];
                var asks = new PriceLevel[book.Count(Side.Ask)];
                var bidCount = book.CopyTo(Side.Bid, bids);
                var askCount = book.CopyTo(Side.Ask, asks);

                if (bidCount != bids.Length || askCount != asks.Length)
                    throw new InvalidOperationException("Book changed while checkpointing.");

                BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(offset), instrumentId);
                BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(offset + 4), bidCount);
                BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(offset + 8), askCount);
                offset += 12;
                offset = WriteLevels(destination, offset, bids);
                offset = WriteLevels(destination, offset, asks);
            }
        }

        private static int WriteLevels(Span<byte> destination, int offset, PriceLevel[] levels)
        {
            foreach (var level in levels)
            {
                if (level.Quantity == 0)
                    throw new InvalidDataException("Checkpoint contains an empty level.");

                BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(offset), level.Price);
                BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(offset + 4), level.Quantity);
                offset += 8;
            }

            return offset;
        }

        private static int ReadSide(ReadOnlySpan<byte> payload, int offset, int count, Side side,
            IOrderBook book)
        {
            EnsureRemaining(payload, offset, checked(count * 8));
            var previous = 0;

            for (var i = 0; i < count; i++)
            {
                var price = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(offset));
                var quantity = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(offset + 4));

                if (quantity == 0 || (i > 0 && (side == Side.Bid ? price >= previous : price <= previous)))
                    throw new InvalidDataException("Checkpoint levels are not canonical.");
                if (!book.Upsert(side, price, quantity))
                    throw new InvalidDataException("Checkpoint level could not be restored.");

                previous = price;
                offset += 8;
            }

            return offset;
        }

        private static void EnsureRemaining(ReadOnlySpan<byte> payload, int offset, int required)
        {
            if (offset < 0 || required < 0 || offset > payload.Length - required)
                throw new InvalidDataException("Checkpoint payload is truncated.");
        }
    }
}
