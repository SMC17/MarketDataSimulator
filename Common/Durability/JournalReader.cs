using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using MarketData.Common.Feed;

namespace MarketData.Common.Durability
{
    public enum RecoveryOutcome
    {
        Clean,
        TruncatedTail,
        Corrupt,
    }

    public sealed record RecoveryReport(
        RecoveryOutcome Outcome,
        long RecordsRead,
        ulong LastSequence,
        ulong LastCheckpointSequence,
        long ValidBytes,
        string DamagedSegment,
        JournalReadResult Failure,
        ulong SessionId = 0,
        ulong NextSequence = 0,
        bool HasSequencedRecords = false,
        long ValidBytesInDamagedSegment = 0)
    {
        public bool Resumable => Outcome != RecoveryOutcome.Corrupt;
    }

    public enum JournalRangeResult : byte
    {
        Success,
        Missing,
        WrongSession,
        Corrupt,
    }

    /// <summary>Streaming, allocation-bounded journal validation and replay.</summary>
    public static class JournalReader
    {
        internal const int ReadBufferSize = 64 * 1024;
        private const int RangeReadBufferSize = 4 * 1024;

        public static RecoveryReport Recover(string directory, RecordHandler onRecord = null,
            ulong fromSequence = Sequencer.None, ulong expectedSessionId = 0,
            ulong? expectedInitialSequence = null)
        {
            var paths = WriteAheadJournal.SegmentFiles(directory);

            if (paths.Count == 0)
                return Clean(0, Sequencer.None, Sequencer.None, 0, 0,
                    expectedInitialSequence ?? Sequencer.None, false);

            var segments = new List<SegmentDescriptor>(paths.Count);
            var priorIndex = -1;
            ulong sessionId = 0;
            string incompleteTail = null;

            for (var i = 0; i < paths.Count; i++)
            {
                var path = paths[i];
                var result = ReadSegmentHeader(path, out var descriptor);

                if (result != JournalReadResult.Ok)
                {
                    if (result == JournalReadResult.Incomplete && i == paths.Count - 1)
                    {
                        incompleteTail = path;
                        break;
                    }

                    return Corrupt(path, result, sessionId);
                }

                if ((priorIndex < 0 && descriptor.Index != 0) ||
                    (priorIndex >= 0 && descriptor.Index != priorIndex + 1))
                    return Corrupt(path, JournalReadResult.SegmentOrder, sessionId);

                if (sessionId == 0)
                    sessionId = descriptor.SessionId;
                else if (descriptor.SessionId != sessionId)
                    return Corrupt(path, JournalReadResult.BadSession, sessionId);

                if (expectedSessionId != 0 && descriptor.SessionId != expectedSessionId)
                    return Corrupt(path, JournalReadResult.BadSession, descriptor.SessionId);

                priorIndex = descriptor.Index;
                segments.Add(descriptor);
            }

            if (segments.Count > 0 && expectedInitialSequence.HasValue &&
                segments[0].FirstSequence != expectedInitialSequence.Value)
                return Corrupt(segments[0].Path, JournalReadResult.SequenceGap, sessionId);

            var firstSegment = SelectFirstSegment(segments, fromSequence);
            long records = 0;
            long validBytes = 0;
            ulong lastSequence = Sequencer.None;
            ulong lastCheckpoint = Sequencer.None;
            var hasSequencedRecords = false;
            var expected = firstSegment < segments.Count
                ? segments[firstSegment].FirstSequence
                : expectedInitialSequence ?? Sequencer.None;

            for (var i = firstSegment; i < segments.Count; i++)
            {
                var segment = segments[i];

                if (i > firstSegment && segment.FirstSequence != expected)
                {
                    return new RecoveryReport(RecoveryOutcome.Corrupt, records, lastSequence,
                        lastCheckpoint, validBytes, segment.Path, JournalReadResult.SequenceGap,
                        sessionId, expected, hasSequencedRecords, 0);
                }

                var report = ScanSegment(segment, i == paths.Count - 1 && incompleteTail is null,
                    sessionId, ref expected, ref hasSequencedRecords, ref lastSequence,
                    ref lastCheckpoint, ref records, ref validBytes, onRecord);

                if (report is not null)
                    return report;
            }

            if (incompleteTail is not null)
            {
                return new RecoveryReport(RecoveryOutcome.TruncatedTail, records, lastSequence,
                    lastCheckpoint, validBytes, incompleteTail, JournalReadResult.Incomplete,
                    sessionId, expected, hasSequencedRecords, 0);
            }

            return Clean(records, lastSequence, lastCheckpoint, validBytes, sessionId,
                expected, hasSequencedRecords);
        }

        public static List<SequencedPayload> ReadRange(string directory, ulong from, ulong to)
        {
            var result = TryReadRange(directory, 0, from, to, out var found);

            if (result == JournalRangeResult.Corrupt)
                throw new InvalidDataException("The journal failed validation.");

            return found;
        }

        public static JournalRangeResult TryReadRange(string directory, ulong sessionId, ulong from,
            ulong to, out List<SequencedPayload> found)
        {
            if (to < from)
                throw new ArgumentOutOfRangeException(nameof(to));

            var results = new List<SequencedPayload>(RangeCapacity(from, to));
            found = results;
            var cursor = from;
            var complete = false;

            var report = Recover(directory, (in JournalRecordView record) =>
            {
                if (!TryGetSequenceRange(record, out var first, out var last, out var count))
                    return true;

                if (last < from)
                    return true;
                if (first > to || first > cursor)
                    return false;

                if (first != cursor || last > to)
                    return false;

                if (last >= cursor)
                {
                    results.Add(new SequencedPayload(first, record.Timestamp, record.Payload.ToArray())
                    {
                        MessageCount = count,
                    });

                    if (last == ulong.MaxValue || last >= to)
                    {
                        complete = true;
                        return false;
                    }

                    cursor = last + 1;
                }

                return true;
            }, from, sessionId);

            if (report.SessionId != 0)
            {
                for (var i = 0; i < results.Count; i++)
                    results[i] = results[i] with { SessionId = report.SessionId };
            }

            if (report.Failure == JournalReadResult.BadSession)
            {
                results.Clear();
                return JournalRangeResult.WrongSession;
            }
            if (report.Outcome == RecoveryOutcome.Corrupt)
            {
                results.Clear();
                return JournalRangeResult.Corrupt;
            }

            if (complete)
                return JournalRangeResult.Success;

            results.Clear();
            return JournalRangeResult.Missing;
        }

        internal static JournalRangeResult TryReadRangeFrom(IReadOnlyList<string> segments,
            int firstSegment, long firstOffset, ulong sessionId, ulong journalExpected,
            ulong from, ulong to, out List<SequencedPayload> found)
        {
            found = new List<SequencedPayload>(RangeCapacity(from, to));
            var cursor = from;
            var expected = journalExpected;

            for (var segmentIndex = firstSegment; segmentIndex < segments.Count; segmentIndex++)
            {
                using var stream = new FileStream(segments[segmentIndex], FileMode.Open,
                    FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1,
                    FileOptions.SequentialScan);
                var limit = stream.Length;

                if (segmentIndex == firstSegment)
                {
                    if (firstOffset < 0 || firstOffset > limit)
                        return JournalRangeResult.Corrupt;
                    stream.Position = firstOffset;
                }

                var reader = new PooledReader(stream, limit, RangeReadBufferSize);

                try
                {
                    if (segmentIndex != firstSegment)
                    {
                        var headerResult = ReadRecord(ref reader, out var headerBuffer,
                            out var headerSize);
                        if (headerResult != JournalReadResult.Ok)
                            return headerResult == JournalReadResult.Incomplete
                                ? JournalRangeResult.Missing
                                : JournalRangeResult.Corrupt;

                        try
                        {
                            JournalRecord.TryRead(headerBuffer.AsSpan(0, headerSize),
                                out var headerRecord);
                            if (headerRecord.Type != JournalRecordType.SegmentHeader ||
                                headerRecord.Payload.Length != 16 ||
                                BinaryPrimitives.ReadUInt64LittleEndian(headerRecord.Payload) !=
                                    sessionId ||
                                BinaryPrimitives.ReadUInt64LittleEndian(
                                    headerRecord.Payload.Slice(8)) != expected)
                                return JournalRangeResult.Corrupt;
                        }
                        finally
                        {
                            ArrayPool<byte>.Shared.Return(headerBuffer);
                        }
                    }

                    while (reader.Position < limit)
                    {
                        var result = ReadRecord(ref reader, out var rented, out var size);
                        if (result != JournalReadResult.Ok)
                            return result == JournalReadResult.Incomplete
                                ? JournalRangeResult.Missing
                                : JournalRangeResult.Corrupt;

                        try
                        {
                            JournalRecord.TryRead(rented.AsSpan(0, size), out var record);

                            if (record.Type is JournalRecordType.Checkpoint or JournalRecordType.Audit)
                                continue;
                            if (!TryGetSequenceRange(record, out var first, out var last,
                                    out var count) || first != expected)
                                return JournalRangeResult.Corrupt;

                            if (record.Type == JournalRecordType.FeedPacket &&
                                (!FeedProtocol.TryReadHeader(record.Payload, out var feedHeader,
                                     out _) || feedHeader.SessionId != sessionId))
                                return JournalRangeResult.Corrupt;

                            expected = last == ulong.MaxValue ? ulong.MaxValue : last + 1;

                            if (last < from)
                                continue;
                            if (first > to || first > cursor || first != cursor || last > to)
                                return JournalRangeResult.Missing;

                            found.Add(new SequencedPayload(first, record.Timestamp,
                                record.Payload.ToArray())
                            {
                                SessionId = sessionId,
                                MessageCount = count,
                            });

                            if (last == ulong.MaxValue || last >= to)
                                return JournalRangeResult.Success;

                            cursor = last + 1;
                        }
                        finally
                        {
                            ArrayPool<byte>.Shared.Return(rented);
                        }
                    }
                }
                finally
                {
                    reader.Dispose();
                }
            }

            return JournalRangeResult.Missing;
        }

        private static RecoveryReport ScanSegment(SegmentDescriptor segment, bool isLastSegment,
            ulong sessionId, ref ulong expected, ref bool hasSequencedRecords, ref ulong lastSequence,
            ref ulong lastCheckpoint, ref long records, ref long validBytes, RecordHandler onRecord)
        {
            using var stream = new FileStream(segment.Path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, bufferSize: 1, FileOptions.SequentialScan);

            var limit = stream.Length;
            long offset = 0;
            var firstRecord = true;
            var reader = new PooledReader(stream, limit);

            try
            {
                while (offset < limit)
                {
                    var result = ReadRecord(ref reader, out var rented, out var size);

                    if (result != JournalReadResult.Ok)
                    {
                        var outcome = result == JournalReadResult.Incomplete && isLastSegment
                            ? RecoveryOutcome.TruncatedTail
                            : RecoveryOutcome.Corrupt;

                        return new RecoveryReport(outcome, records, lastSequence, lastCheckpoint,
                            validBytes, segment.Path, result, sessionId, expected,
                            hasSequencedRecords, offset);
                    }

                    try
                    {
                        JournalRecord.TryRead(rented.AsSpan(0, size), out var record);

                        if (firstRecord)
                        {
                            firstRecord = false;
                            if (!IsExpectedHeader(record, segment))
                            {
                                return new RecoveryReport(RecoveryOutcome.Corrupt, records,
                                    lastSequence, lastCheckpoint, validBytes, segment.Path,
                                    JournalReadResult.SegmentOrder, sessionId, expected,
                                    hasSequencedRecords, offset);
                            }
                        }
                        else if (!ApplySequenceContract(record, sessionId, ref expected,
                                     ref hasSequencedRecords, ref lastSequence, ref lastCheckpoint,
                                     out var semanticFailure))
                        {
                            return new RecoveryReport(RecoveryOutcome.Corrupt, records, lastSequence,
                                lastCheckpoint, validBytes, segment.Path, semanticFailure,
                                sessionId, expected, hasSequencedRecords, offset);
                        }

                        records++;
                        validBytes += size;
                        offset += size;

                        if (onRecord is not null && !onRecord(record))
                            return Clean(records, lastSequence, lastCheckpoint, validBytes, sessionId,
                                expected, hasSequencedRecords);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(rented);
                    }
                }
            }
            finally
            {
                reader.Dispose();
            }

            return null;
        }

        private static bool ApplySequenceContract(in JournalRecordView record, ulong sessionId,
            ref ulong expected, ref bool hasSequencedRecords, ref ulong lastSequence,
            ref ulong lastCheckpoint, out JournalReadResult failure)
        {
            failure = JournalReadResult.Ok;

            switch (record.Type)
            {
                case JournalRecordType.Message:
                    if (record.Sequence != expected || expected == ulong.MaxValue)
                    {
                        failure = JournalReadResult.SequenceGap;
                        return false;
                    }

                    lastSequence = record.Sequence;
                    hasSequencedRecords = true;
                    expected++;
                    return true;

                case JournalRecordType.FeedPacket:
                    if (!FeedProtocol.TryReadHeader(record.Payload, out var header, out _) ||
                        header.SessionId != sessionId || header.FirstSequence != record.Sequence ||
                        header.FirstSequence != expected)
                    {
                        failure = header.SessionId != 0 && header.SessionId != sessionId
                            ? JournalReadResult.BadSession
                            : JournalReadResult.SequenceGap;
                        return false;
                    }

                    expected = header.FirstSequence + header.MessageCount;
                    lastSequence = expected - 1;
                    hasSequencedRecords = true;
                    return true;

                case JournalRecordType.Checkpoint:
                    if (record.Payload.Length != sizeof(ulong) ||
                        BinaryPrimitives.ReadUInt64LittleEndian(record.Payload) != record.Sequence ||
                        (hasSequencedRecords ? record.Sequence > lastSequence :
                            record.Sequence != Sequencer.None))
                    {
                        failure = JournalReadResult.SequenceGap;
                        return false;
                    }

                    lastCheckpoint = record.Sequence;
                    return true;

                case JournalRecordType.Audit:
                    if (hasSequencedRecords ? record.Sequence > lastSequence :
                        record.Sequence != Sequencer.None)
                    {
                        failure = JournalReadResult.SequenceGap;
                        return false;
                    }

                    return true;

                default:
                    failure = JournalReadResult.BadType;
                    return false;
            }
        }

        private static bool TryGetSequenceRange(in JournalRecordView record, out ulong first,
            out ulong last, out ushort count)
        {
            first = record.Sequence;
            last = record.Sequence;
            count = 1;

            if (record.Type == JournalRecordType.Message)
                return true;
            if (record.Type != JournalRecordType.FeedPacket ||
                !FeedProtocol.TryReadHeader(record.Payload, out var header, out _))
                return false;

            first = header.FirstSequence;
            count = header.MessageCount;
            last = first + count - 1;
            return true;
        }

        private static int RangeCapacity(ulong from, ulong to)
        {
            var distance = to - from;
            return distance >= 255 ? 256 : (int)distance + 1;
        }

        internal static JournalReadResult ReadRecord(ref PooledReader reader,
            out byte[] rented, out int size)
        {
            rented = null;
            size = 0;
            Span<byte> header = stackalloc byte[JournalRecord.HeaderSize];

            if (!reader.ReadExactly(header))
                return JournalReadResult.Incomplete;

            var headerResult = JournalRecord.TryGetSize(header, out size);
            if (headerResult != JournalReadResult.Ok)
                return headerResult;

            rented = ArrayPool<byte>.Shared.Rent(size);
            header.CopyTo(rented);

            if (!reader.ReadExactly(rented.AsSpan(JournalRecord.HeaderSize,
                    size - JournalRecord.HeaderSize)))
            {
                ArrayPool<byte>.Shared.Return(rented);
                rented = null;
                return JournalReadResult.Incomplete;
            }

            var result = JournalRecord.TryRead(rented.AsSpan(0, size), out _);
            if (result != JournalReadResult.Ok)
            {
                ArrayPool<byte>.Shared.Return(rented);
                rented = null;
            }

            return result;
        }

        internal static JournalReadResult ReadRecord(FileStream stream, long limit,
            out byte[] rented, out int size)
        {
            rented = null;
            size = 0;
            Span<byte> header = stackalloc byte[JournalRecord.HeaderSize];

            if (!ReadExactly(stream, header, limit))
                return JournalReadResult.Incomplete;

            var headerResult = JournalRecord.TryGetSize(header, out size);
            if (headerResult != JournalReadResult.Ok)
                return headerResult;

            rented = ArrayPool<byte>.Shared.Rent(size);
            header.CopyTo(rented);

            if (!ReadExactly(stream, rented.AsSpan(JournalRecord.HeaderSize,
                    size - JournalRecord.HeaderSize), limit))
            {
                ArrayPool<byte>.Shared.Return(rented);
                rented = null;
                return JournalReadResult.Incomplete;
            }

            var result = JournalRecord.TryRead(rented.AsSpan(0, size), out _);
            if (result != JournalReadResult.Ok)
            {
                ArrayPool<byte>.Shared.Return(rented);
                rented = null;
            }

            return result;
        }

        private static bool ReadExactly(FileStream stream, Span<byte> destination, long limit)
        {
            var read = 0;

            while (read < destination.Length && stream.Position < limit)
            {
                var available = (int)Math.Min(destination.Length - read, limit - stream.Position);
                var got = stream.Read(destination.Slice(read, available));
                if (got == 0)
                    break;
                read += got;
            }

            return read == destination.Length;
        }

        internal struct PooledReader : IDisposable
        {
            private readonly FileStream _stream;
            private readonly long _limit;
            private byte[] _buffer;
            private int _offset;
            private int _count;
            private long _position;

            public PooledReader(FileStream stream, long limit, int bufferSize = ReadBufferSize)
            {
                _stream = stream;
                _limit = limit;
                _buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
                _offset = 0;
                _count = 0;
                _position = stream.Position;
            }

            public readonly long Position => _position;

            public bool ReadExactly(Span<byte> destination)
            {
                var written = 0;

                while (written < destination.Length)
                {
                    if (_offset == _count && !Fill())
                        return false;

                    var available = Math.Min(destination.Length - written, _count - _offset);
                    _buffer.AsSpan(_offset, available).CopyTo(destination.Slice(written));
                    _offset += available;
                    _position += available;
                    written += available;
                }

                return true;
            }

            private bool Fill()
            {
                _offset = 0;
                _count = 0;

                if (_position >= _limit)
                    return false;

                var wanted = (int)Math.Min(_buffer.Length, _limit - _position);

                while (_count < wanted)
                {
                    var read = _stream.Read(_buffer.AsSpan(_count, wanted - _count));
                    if (read == 0)
                        break;
                    _count += read;
                }

                return _count != 0;
            }

            public void Dispose()
            {
                var buffer = _buffer;
                _buffer = null;
                if (buffer is not null)
                    ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private static JournalReadResult ReadSegmentHeader(string path, out SegmentDescriptor descriptor)
        {
            descriptor = default;

            if (!TryParseSegmentIndex(path, out var index))
                return JournalReadResult.SegmentOrder;

            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, bufferSize: 1,
                    FileOptions.SequentialScan);
                var result = ReadRecord(stream, stream.Length, out var rented, out var size);

                if (result != JournalReadResult.Ok)
                    return result;

                try
                {
                    JournalRecord.TryRead(rented.AsSpan(0, size), out var record);

                    if (record.Type != JournalRecordType.SegmentHeader || record.Payload.Length != 16)
                        return JournalReadResult.SegmentOrder;

                    var session = BinaryPrimitives.ReadUInt64LittleEndian(record.Payload);
                    if (session == 0)
                        return JournalReadResult.BadSession;

                    descriptor = new SegmentDescriptor(path, index, session,
                        BinaryPrimitives.ReadUInt64LittleEndian(record.Payload.Slice(8)), size);
                    return JournalReadResult.Ok;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(rented);
                }
            }
            catch (IOException)
            {
                return JournalReadResult.Incomplete;
            }
        }

        private static bool IsExpectedHeader(in JournalRecordView record, SegmentDescriptor segment)
            => record.Type == JournalRecordType.SegmentHeader && record.TotalSize == segment.HeaderSize &&
               record.Payload.Length == 16 &&
               BinaryPrimitives.ReadUInt64LittleEndian(record.Payload) == segment.SessionId &&
               BinaryPrimitives.ReadUInt64LittleEndian(record.Payload.Slice(8)) == segment.FirstSequence;

        private static int SelectFirstSegment(List<SegmentDescriptor> segments, ulong fromSequence)
        {
            var first = 0;

            if (fromSequence == Sequencer.None)
                return first;

            for (var i = 0; i + 1 < segments.Count; i++)
            {
                if (segments[i + 1].FirstSequence <= fromSequence)
                    first = i + 1;
                else
                    break;
            }

            return first;
        }

        private static bool TryParseSegmentIndex(string path, out int index)
        {
            const string prefix = "segment-";
            var name = Path.GetFileNameWithoutExtension(path);
            index = -1;

            return name.StartsWith(prefix, StringComparison.Ordinal) &&
                int.TryParse(name.AsSpan(prefix.Length), out index) && index >= 0;
        }

        private static RecoveryReport Clean(long records, ulong lastSequence, ulong lastCheckpoint,
            long validBytes, ulong sessionId, ulong nextSequence, bool hasSequencedRecords)
            => new(RecoveryOutcome.Clean, records, lastSequence, lastCheckpoint, validBytes,
                null, JournalReadResult.Ok, sessionId, nextSequence, hasSequencedRecords, 0);

        private static RecoveryReport Corrupt(string path, JournalReadResult failure, ulong sessionId)
            => new(RecoveryOutcome.Corrupt, 0, Sequencer.None, Sequencer.None, 0, path, failure,
                sessionId, 0, false, 0);

        private readonly record struct SegmentDescriptor(string Path, int Index, ulong SessionId,
            ulong FirstSequence, int HeaderSize);

        public delegate bool RecordHandler(in JournalRecordView record);
    }

    public sealed record SequencedPayload(ulong Sequence, long Timestamp, byte[] Payload)
    {
        public ulong SessionId { get; init; }
        public ushort MessageCount { get; init; } = 1;
    }
}
