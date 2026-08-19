using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace MarketData.Common.Durability
{
    /// <summary>When an append is considered durable.</summary>
    /// <remarks>
    /// This is the honest name for a trade-off that is usually hidden. Every option below loses
    /// data under some failure; they differ in which failure.
    /// </remarks>
    public enum DurabilityPolicy
    {
        /// <summary>
        /// Hand the bytes to the OS and return. Survives process death, loses whatever the page
        /// cache held if the machine dies.
        /// </summary>
        OsBuffered,

        /// <summary>
        /// fsync every append. Survives machine death up to the last returned append, and costs a
        /// device round trip per record.
        /// </summary>
        SyncEachRecord,

        /// <summary>
        /// fsync at most every <see cref="WriteAheadJournal.FlushInterval"/>. Bounds the loss
        /// window by time rather than eliminating it - the usual choice, and the one that must
        /// state its window out loud.
        /// </summary>
        SyncPeriodic,
    }

    /// <summary>
    /// An append-only, CRC-checked, segmented log of sequenced records.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Write-ahead means what it says: a message is journalled before it is published. A subscriber
    /// can then never hold a message the publisher cannot reproduce, which is what makes
    /// retransmission and backup takeover possible at all. Publish-then-journal would invert that
    /// and create messages that exist only in flight.
    /// </para>
    /// <para>
    /// Segments rather than one file, because retention and recovery both work in whole files:
    /// old segments are deleted without rewriting anything, and a damaged segment bounds the
    /// damage. Each segment opens with a header record naming the session and its first sequence,
    /// so a segment is self-describing and recovery does not depend on file names.
    /// </para>
    /// </remarks>
    public sealed class WriteAheadJournal : IDisposable
    {
        /// <summary>How often <see cref="DurabilityPolicy.SyncPeriodic"/> reaches the device.</summary>
        public static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(200);

        private const string SegmentPrefix = "segment-";
        private const string SegmentSuffix = ".jrn";

        private readonly object _gate = new();
        private readonly string _directory;
        private readonly long _segmentBytes;
        private readonly ulong _sessionId;

        private FileStream _segment;
        private long _segmentLength;
        private int _segmentIndex;
        private long _lastFlushTicks;
        private bool _disposed;

        public DurabilityPolicy Policy { get; }

        /// <summary>Highest sequence written, or <see cref="Sequencer.None"/>.</summary>
        public ulong LastSequence { get; private set; }

        /// <summary>Records appended since this journal was opened.</summary>
        public long RecordsAppended { get; private set; }

        /// <summary>Times the log has actually been forced to the device.</summary>
        public long Syncs { get; private set; }

        public WriteAheadJournal(string directory, ulong sessionId,
            DurabilityPolicy policy = DurabilityPolicy.SyncPeriodic, long segmentBytes = 64L * 1024 * 1024)
        {
            if (sessionId == 0)
                throw new ArgumentOutOfRangeException(nameof(sessionId), "Session id 0 is reserved.");
            if (segmentBytes < JournalRecord.OverheadSize + JournalRecord.MaxPayloadSize)
                throw new ArgumentOutOfRangeException(nameof(segmentBytes),
                    "A segment must be able to hold at least one maximum-sized record.");

            _directory = directory;
            _sessionId = sessionId;
            _segmentBytes = segmentBytes;
            Policy = policy;

            Directory.CreateDirectory(directory);

            var existing = SegmentFiles(directory);
            _segmentIndex = existing.Count == 0 ? 0 : IndexOf(existing[^1]) + 1;

            OpenSegment();
        }

        /// <summary>Appends one record and returns the sequence it was written under.</summary>
        public ulong Append(JournalRecordType type, ulong sequence, long timestamp, ReadOnlySpan<byte> payload)
        {
            var size = JournalRecord.SizeFor(payload.Length);
            var buffer = ArrayPool<byte>.Shared.Rent(size);

            try
            {
                JournalRecord.Write(buffer, type, sequence, timestamp, payload);

                lock (_gate)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);

                    // Rotate before writing, never mid-record: a record split across two segments
                    // could not be validated by either.
                    if (_segmentLength + size > _segmentBytes)
                        RollSegment();

                    _segment.Write(buffer, 0, size);
                    _segmentLength += size;
                    RecordsAppended++;

                    if (sequence > LastSequence)
                        LastSequence = sequence;

                    ApplyDurabilityPolicy();
                }

                return sequence;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        /// <summary>Forces everything appended so far to the device.</summary>
        public void Sync()
        {
            lock (_gate)
            {
                if (_disposed)
                    return;

                ForceToDevice();
            }
        }

        private void ApplyDurabilityPolicy()
        {
            switch (Policy)
            {
                case DurabilityPolicy.SyncEachRecord:
                    ForceToDevice();
                    break;

                case DurabilityPolicy.SyncPeriodic:
                    var now = Environment.TickCount64;
                    if (now - _lastFlushTicks >= FlushInterval.TotalMilliseconds)
                        ForceToDevice();
                    break;

                case DurabilityPolicy.OsBuffered:
                    // Deliberately nothing. The bytes are in the page cache and survive this
                    // process dying, which is the guarantee this policy offers and the only one.
                    break;
            }
        }

        private void ForceToDevice()
        {
            // flushToDisk: true is the part that matters. Stream.Flush() alone only moves bytes
            // from the managed buffer into the OS, which is not durability, and is the usual way
            // a journal turns out not to be one.
            _segment.Flush(flushToDisk: true);
            _lastFlushTicks = Environment.TickCount64;
            Syncs++;
        }

        private void RollSegment()
        {
            ForceToDevice();
            _segment.Dispose();
            _segmentIndex++;
            OpenSegment();
        }

        private void OpenSegment()
        {
            var path = Path.Combine(_directory, $"{SegmentPrefix}{_segmentIndex:D9}{SegmentSuffix}");

            _segment = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read,
                bufferSize: 64 * 1024, FileOptions.SequentialScan);
            _segmentLength = 0;
            _lastFlushTicks = Environment.TickCount64;

            // A segment names itself, so recovery never has to trust a file name.
            Span<byte> header = stackalloc byte[16];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(header, _sessionId);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(header.Slice(8),
                LastSequence + 1);

            var size = JournalRecord.SizeFor(header.Length);
            var buffer = ArrayPool<byte>.Shared.Rent(size);

            try
            {
                JournalRecord.Write(buffer, JournalRecordType.SegmentHeader, LastSequence,
                    DateTime.UtcNow.Ticks, header);
                _segment.Write(buffer, 0, size);
                _segmentLength += size;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        internal static List<string> SegmentFiles(string directory)
            => Directory.Exists(directory)
                ? Directory.GetFiles(directory, SegmentPrefix + "*" + SegmentSuffix).OrderBy(f => f).ToList()
                : new List<string>();

        private static int IndexOf(string path)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            return int.TryParse(name.AsSpan(SegmentPrefix.Length), out var index) ? index : 0;
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                    return;

                _disposed = true;

                try
                {
                    ForceToDevice();
                }
                finally
                {
                    _segment.Dispose();
                }
            }
        }
    }
}
