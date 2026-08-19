using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;

namespace MarketData.Common.Durability
{
    /// <summary>What recovery found at the end of the log.</summary>
    public enum RecoveryOutcome
    {
        /// <summary>Every record in every segment validated.</summary>
        Clean,

        /// <summary>
        /// The final record was incomplete. Expected after a crash mid-append, and recoverable:
        /// everything before it is intact and the partial tail is discarded.
        /// </summary>
        TruncatedTail,

        /// <summary>
        /// A record failed validation with more data after it. This is not a torn tail - it is
        /// damage inside the log - and it bounds what can be trusted.
        /// </summary>
        Corrupt,
    }

    public sealed record RecoveryReport(
        RecoveryOutcome Outcome,
        long RecordsRead,
        ulong LastSequence,
        ulong LastCheckpointSequence,
        long ValidBytes,
        string DamagedSegment,
        JournalReadResult Failure)
    {
        /// <summary>Whether the log can be resumed without operator intervention.</summary>
        public bool Resumable => Outcome != RecoveryOutcome.Corrupt;
    }

    /// <summary>Reads a journal back, for recovery, catch-up and retransmission.</summary>
    public static class JournalReader
    {
        /// <summary>
        /// Scans every segment, validating as it goes.
        /// </summary>
        /// <param name="onRecord">
        /// Invoked for each valid record in sequence order. Return false to stop early.
        /// </param>
        /// <remarks>
        /// <para>
        /// The distinction this method exists to draw is between a <em>torn tail</em> and
        /// <em>corruption</em>. A process killed mid-append leaves a partial record at the very end
        /// of the last segment; that is normal, expected, and safe to discard, because nothing was
        /// ever acknowledged from it. A record that fails to validate with more data after it means
        /// something overwrote the middle of the log, and silently skipping it would hand callers a
        /// log with an invisible hole.
        /// </para>
        /// <para>
        /// So the two are reported differently and only the first is recoverable. Anything else is
        /// a decision for an operator, not for a library.
        /// </para>
        /// </remarks>
        public static RecoveryReport Recover(string directory, RecordHandler onRecord = null,
            ulong fromSequence = Sequencer.None)
        {
            var segments = WriteAheadJournal.SegmentFiles(directory);

            // Skip whole segments that end before the caller's starting point. Without this,
            // restoring from a checkpoint still reads and checksums the entire history behind it,
            // so recovery stays O(uptime) and the checkpoint buys almost nothing - which is what
            // the first version of this actually did, and the benchmark caught it.
            var firstSegment = 0;

            if (fromSequence != Sequencer.None)
            {
                for (var s = 0; s + 1 < segments.Count; s++)
                {
                    // A segment can be skipped only once the *next* segment is known to start at
                    // or before the target: that proves this one holds nothing the caller wants.
                    var nextStart = SegmentFirstSequence(segments[s + 1]);

                    if (nextStart != Sequencer.None && nextStart <= fromSequence)
                        firstSegment = s + 1;
                    else
                        break;
                }
            }

            segments = segments.GetRange(firstSegment, segments.Count - firstSegment);

            long records = 0;
            long validBytes = 0;
            ulong lastSequence = Sequencer.None;
            ulong lastCheckpoint = Sequencer.None;

            for (var s = 0; s < segments.Count; s++)
            {
                var path = segments[s];
                var isLastSegment = s == segments.Count - 1;
                var bytes = File.ReadAllBytes(path);
                var offset = 0;

                while (offset < bytes.Length)
                {
                    var result = JournalRecord.TryRead(bytes.AsSpan(offset), out var record);

                    if (result == JournalReadResult.Ok)
                    {
                        records++;
                        validBytes += record.TotalSize;

                        if (record.Type == JournalRecordType.Checkpoint)
                            lastCheckpoint = record.Sequence;

                        if (record.Type != JournalRecordType.SegmentHeader && record.Sequence > lastSequence)
                            lastSequence = record.Sequence;

                        if (onRecord is not null && !onRecord(record))
                            return new RecoveryReport(RecoveryOutcome.Clean, records, lastSequence,
                                lastCheckpoint, validBytes, null, JournalReadResult.Ok);

                        offset += record.TotalSize;
                        continue;
                    }

                    // A short read at the very end of the very last segment is a torn tail.
                    // Anywhere else, the log has a hole in the middle and callers must be told.
                    var atEndOfLastSegment = isLastSegment;

                    var outcome = result == JournalReadResult.Incomplete && atEndOfLastSegment
                        ? RecoveryOutcome.TruncatedTail
                        : RecoveryOutcome.Corrupt;

                    return new RecoveryReport(outcome, records, lastSequence, lastCheckpoint,
                        validBytes, path, result);
                }
            }

            return new RecoveryReport(RecoveryOutcome.Clean, records, lastSequence, lastCheckpoint,
                validBytes, null, JournalReadResult.Ok);
        }

        /// <summary>
        /// Reads the messages in <c>[from, to]</c>, which is what a gap-fill request asks for.
        /// </summary>
        /// <remarks>
        /// Returns only <see cref="JournalRecordType.Message"/> records: a subscriber filling a gap
        /// wants the market data it missed, not the journal's own bookkeeping.
        /// </remarks>
        public static List<SequencedPayload> ReadRange(string directory, ulong from, ulong to)
        {
            if (to < from)
                throw new ArgumentOutOfRangeException(nameof(to), "Range ends before it starts.");

            var found = new List<SequencedPayload>();

            Recover(directory, (in JournalRecordView record) =>
            {
                if (record.Type == JournalRecordType.Message &&
                    record.Sequence >= from && record.Sequence <= to)
                {
                    found.Add(new SequencedPayload(record.Sequence, record.Timestamp,
                        record.Payload.ToArray()));
                }

                // Sequences ascend, so once past the range there is nothing left to find.
                return record.Sequence <= to;
            });

            return found;
        }

        /// <summary>
        /// Reads the sequence a segment starts at, from its own header record.
        /// </summary>
        /// <remarks>
        /// Read from the file's contents rather than parsed out of its name. The name is a
        /// convenience for humans and for ordering; trusting it for correctness would make a
        /// renamed or copied file silently wrong.
        /// </remarks>
        private static ulong SegmentFirstSequence(string path)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

                var header = new byte[JournalRecord.SizeFor(16)];
                var read = 0;

                while (read < header.Length)
                {
                    var got = stream.Read(header, read, header.Length - read);
                    if (got == 0) break;
                    read += got;
                }

                if (read < header.Length)
                    return Sequencer.None;

                if (JournalRecord.TryRead(header, out var record) != JournalReadResult.Ok ||
                    record.Type != JournalRecordType.SegmentHeader ||
                    record.Payload.Length < 16)
                {
                    return Sequencer.None;
                }

                return System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(
                    record.Payload.Slice(8, 8));
            }
            catch (IOException)
            {
                return Sequencer.None;
            }
        }

        /// <summary>Handler invoked per record. Return false to stop the scan.</summary>
        public delegate bool RecordHandler(in JournalRecordView record);
    }

    /// <summary>A recovered message, copied out of the log so it outlives the scan.</summary>
    public sealed record SequencedPayload(ulong Sequence, long Timestamp, byte[] Payload);
}
