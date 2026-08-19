using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MarketData.Common.Books;
using MarketData.Common.Feed;

namespace MarketData.Common.Durability
{
    /// <summary>
    /// A complete book state captured at one sequence, so recovery need not replay from zero.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without checkpoints, recovery time grows without bound with uptime: a log that has been
    /// running for a week takes a week's worth of replay to rebuild. That is the difference between
    /// a system that can be restarted during the day and one that cannot.
    /// </para>
    /// <para>
    /// The invariant that makes this safe is precise: a checkpoint at sequence S plus every
    /// journalled message after S must reconstruct exactly the same state as replaying every
    /// message from the beginning. <c>CheckpointTests</c> asserts that equivalence directly rather
    /// than trusting it, because a checkpoint that is subtly wrong is worse than none - it produces
    /// a book that is confidently incorrect.
    /// </para>
    /// <para>
    /// Checkpoints are written to their own files rather than inline in the log, and a marker
    /// record goes into the log pointing at them. That keeps the log append-only and lets an old
    /// checkpoint be deleted without rewriting anything.
    /// </para>
    /// </remarks>
    public static class Checkpoint
    {
        public const uint Magic = 0x43484B31; // "CHK1"
        private const string Prefix = "checkpoint-";
        private const string Suffix = ".chk";

        /// <summary>Writes a checkpoint and records a marker in the journal.</summary>
        public static string Write(string directory, WriteAheadJournal journal, ulong sequence,
            ulong sessionId, IReadOnlyDictionary<int, IOrderBook> books)
        {
            Directory.CreateDirectory(directory);

            var path = Path.Combine(directory, $"{Prefix}{sequence:D20}{Suffix}");
            var temporary = path + ".tmp";

            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(Magic);
                writer.Write(sessionId);
                writer.Write(sequence);
                writer.Write(books.Count);

                foreach (var (instrumentId, book) in books.OrderBy(entry => entry.Key))
                {
                    writer.Write(instrumentId);
                    WriteSide(writer, book, Side.Bid);
                    WriteSide(writer, book, Side.Ask);
                }

                stream.Flush(flushToDisk: true);
            }

            // Rename last, and only once the bytes are on the device. A checkpoint file that
            // exists is therefore always complete: a crash mid-write leaves a .tmp that recovery
            // ignores, rather than a truncated checkpoint that recovery would trust.
            File.Move(temporary, path, overwrite: true);

            Span<byte> marker = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(marker, sequence);
            journal.Append(JournalRecordType.Checkpoint, sequence, DateTime.UtcNow.Ticks, marker);
            journal.Sync();

            return path;
        }

        private static void WriteSide(BinaryWriter writer, IOrderBook book, Side side)
        {
            var count = book.Count(side);
            var levels = new PriceLevel[count];
            var copied = book.CopyTo(side, levels);

            writer.Write(copied);

            for (var i = 0; i < copied; i++)
            {
                writer.Write(levels[i].Price);
                writer.Write(levels[i].Quantity);
            }
        }

        /// <summary>The newest checkpoint at or below <paramref name="notAfter"/>, if any.</summary>
        public static string FindLatest(string directory, ulong notAfter = ulong.MaxValue)
        {
            if (!Directory.Exists(directory))
                return null;

            return Directory.GetFiles(directory, Prefix + "*" + Suffix)
                .Select(path => (path, sequence: SequenceOf(path)))
                .Where(entry => entry.sequence != Sequencer.None && entry.sequence <= notAfter)
                .OrderByDescending(entry => entry.sequence)
                .Select(entry => entry.path)
                .FirstOrDefault();
        }

        public static ulong SequenceOf(string path)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            return name.StartsWith(Prefix, StringComparison.Ordinal)
                   && ulong.TryParse(name.AsSpan(Prefix.Length), out var sequence)
                ? sequence
                : Sequencer.None;
        }

        /// <summary>Restores books from a checkpoint file.</summary>
        /// <returns>The sequence the state is current as of.</returns>
        public static ulong Restore(string path, Func<int, IOrderBook> bookFactory,
            IDictionary<int, IOrderBook> books)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream);

            if (reader.ReadUInt32() != Magic)
                throw new InvalidDataException($"{path} is not a checkpoint.");

            reader.ReadUInt64(); // session id, retained for provenance
            var sequence = reader.ReadUInt64();
            var instruments = reader.ReadInt32();

            for (var i = 0; i < instruments; i++)
            {
                var instrumentId = reader.ReadInt32();
                var book = bookFactory(instrumentId);
                book.Clear();

                ReadSide(reader, book, Side.Bid);
                ReadSide(reader, book, Side.Ask);

                books[instrumentId] = book;
            }

            return sequence;
        }

        private static void ReadSide(BinaryReader reader, IOrderBook book, Side side)
        {
            var count = reader.ReadInt32();

            for (var i = 0; i < count; i++)
            {
                var price = reader.ReadInt32();
                var quantity = reader.ReadUInt32();
                book.Upsert(side, price, quantity);
            }
        }

        /// <summary>
        /// Deletes checkpoints older than the newest <paramref name="keep"/>.
        /// </summary>
        /// <remarks>
        /// Never deletes the newest, whatever <paramref name="keep"/> says: a directory with no
        /// checkpoint at all is exactly the unbounded-recovery situation checkpoints exist to
        /// prevent, and retention should not be able to cause it.
        /// </remarks>
        public static int Prune(string directory, int keep = 3)
        {
            if (keep < 1)
                throw new ArgumentOutOfRangeException(nameof(keep), keep, "Must keep at least one.");

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
    }
}
