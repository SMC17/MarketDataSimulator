using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using MarketData.Common.Durability;

namespace MarketData.Common.Risk
{
    /// <summary>Kinds of auditable non-market event.</summary>
    public enum AuditEventType : byte
    {
        Invalid = 0,
        OrderAccepted = 1,
        OrderRejected = 2,
        Fill = 3,
        KillSwitchEngaged = 4,
        KillSwitchReleased = 5,
        LimitsChanged = 6,
        EntitlementChanged = 7,
        SessionStateChanged = 8,
    }

    public sealed record AuditEvent(
        ulong Sequence,
        DateTime TimestampUtc,
        AuditEventType Type,
        string ParticipantId,
        int InstrumentId,
        RiskRejectReason Reason,
        long Detail);

    /// <summary>
    /// An append-only record of every risk decision, on the same journal as market data.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Sharing the journal is the point rather than a convenience. An audit trail in a separate
    /// store has to be reconciled against the market data to answer the only question anyone
    /// actually asks - what did the system know, and what did it do, at this sequence number - and
    /// reconciling two logs written by two mechanisms is exactly where the answer goes missing. One
    /// log, one order, one CRC.
    /// </para>
    /// <para>
    /// An audit event carries the sequence of the last durable message rather than one of its own,
    /// so it reads as "at this point in the stream, this decision was made". That is both the more
    /// useful statement and the only one the journal will accept: sequence numbers belong to the
    /// message stream, and an annotation that consumed them would leave gaps subscribers would
    /// report as loss.
    /// </para>
    /// <para>
    /// Retention is by policy and enforced by deletion of whole segments, never by rewriting. An
    /// audit log that can be edited in place is not one.
    /// </para>
    /// </remarks>
    public sealed class AuditLog
    {
        private readonly WriteAheadJournal _journal;

        public AuditLog(WriteAheadJournal journal)
            => _journal = journal ?? throw new ArgumentNullException(nameof(journal));

        public long EventsWritten { get; private set; }

        public ulong Record(AuditEventType type, string participantId, int instrumentId = 0,
            RiskRejectReason reason = RiskRejectReason.None, long detail = 0)
        {
            var participant = participantId ?? string.Empty;
            var nameBytes = Encoding.UTF8.GetByteCount(participant);

            if (nameBytes > byte.MaxValue)
                throw new ArgumentException("Participant id is too long to audit.", nameof(participantId));

            var size = 1 + 8 + 4 + 1 + 8 + 1 + nameBytes;
            var buffer = ArrayPool<byte>.Shared.Rent(size);

            try
            {
                var span = buffer.AsSpan(0, size);
                span[0] = (byte)type;
                BinaryPrimitives.WriteInt64LittleEndian(span.Slice(1, 8), DateTime.UtcNow.Ticks);
                BinaryPrimitives.WriteInt32LittleEndian(span.Slice(9, 4), instrumentId);
                span[13] = (byte)reason;
                BinaryPrimitives.WriteInt64LittleEndian(span.Slice(14, 8), detail);
                span[22] = (byte)nameBytes;
                Encoding.UTF8.GetBytes(participant, span.Slice(23));

                // An audit event annotates a point in the message stream; it does not occupy one.
                // Taking its own number from a separate sequencer - which an earlier version did -
                // both inflates the message sequence space and produces audit records pointing at
                // sequences that never existed. The journal rejects that outright, and is right to.
                var sequence = _journal.LastSequence;

                _journal.Append(JournalRecordType.Audit, sequence, DateTime.UtcNow.Ticks, span);
                EventsWritten++;

                return sequence;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        /// <summary>Reads back every audit event, in sequence order.</summary>
        public static IReadOnlyList<AuditEvent> ReadAll(string journalDirectory)
        {
            var events = new List<AuditEvent>();

            JournalReader.Recover(journalDirectory, (in JournalRecordView record) =>
            {
                if (record.Type != JournalRecordType.Audit)
                    return true;

                var payload = record.Payload;

                if (payload.Length < 23)
                    return true;

                var nameLength = payload[22];

                if (payload.Length < 23 + nameLength)
                    return true;

                events.Add(new AuditEvent(
                    record.Sequence,
                    new DateTime(BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(1, 8)), DateTimeKind.Utc),
                    (AuditEventType)payload[0],
                    Encoding.UTF8.GetString(payload.Slice(23, nameLength)),
                    BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(9, 4)),
                    (RiskRejectReason)payload[13],
                    BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(14, 8))));

                return true;
            });

            return events;
        }
    }

    /// <summary>How long audit history is kept, and what enforces it.</summary>
    /// <remarks>
    /// Expressed in whole segments because that is the only unit that can be deleted without
    /// rewriting the log, and rewriting an audit log defeats its purpose. A retention policy
    /// stated in records rather than segments would require exactly the edit it must never make.
    /// </remarks>
    public sealed record RetentionPolicy(TimeSpan Keep, int MinimumSegments = 2)
    {
        public static RetentionPolicy SevenYears { get; } = new(TimeSpan.FromDays(365 * 7));

        /// <summary>
        /// Deletes whole segments older than the policy allows.
        /// </summary>
        /// <returns>Segments removed.</returns>
        public int Enforce(string journalDirectory, DateTime nowUtc)
        {
            var segments = System.IO.Directory.Exists(journalDirectory)
                ? new List<string>(System.IO.Directory.GetFiles(journalDirectory, "segment-*.jrn"))
                : new List<string>();

            segments.Sort(StringComparer.Ordinal);

            var removed = 0;
            var cutoff = nowUtc - Keep;

            // Never below the floor, and never the newest: a log with no segments cannot be
            // appended to or recovered from, and retention should not be able to cause that.
            for (var i = 0; i < segments.Count - MinimumSegments; i++)
            {
                if (System.IO.File.GetLastWriteTimeUtc(segments[i]) >= cutoff)
                    break;

                System.IO.File.Delete(segments[i]);
                removed++;
            }

            return removed;
        }
    }
}
