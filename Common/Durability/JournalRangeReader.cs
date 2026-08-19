using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using MarketData.Common.Feed;

namespace MarketData.Common.Durability
{
    /// <summary>Sparse in-memory sequence index with incremental tail refresh.</summary>
    public sealed class JournalRangeReader
    {
        public const int DefaultStride = 256;

        private readonly object _gate = new();
        private readonly string _directory;
        private readonly int _stride;
        private readonly List<IndexEntry> _entries = new();

        private List<string> _segments = new();
        private int _scanSegment = -1;
        private long _scanOffset;
        private long _sequencedRecords;
        private ulong _nextSequence;

        public JournalRangeReader(string directory, int stride = DefaultStride)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(directory);
            if (stride is < 1 or > 65_536)
                throw new ArgumentOutOfRangeException(nameof(stride));

            _directory = directory;
            _stride = stride;
            Build();
        }

        public ulong SessionId { get; private set; }
        public int IndexEntries { get { lock (_gate) return _entries.Count; } }

        public JournalRangeResult TryRead(ulong sessionId, ulong from, ulong to,
            out List<SequencedPayload> messages)
        {
            if (to < from)
                throw new ArgumentOutOfRangeException(nameof(to));

            IReadOnlyList<string> segments;
            IndexEntry entry;
            ulong indexedSession;

            lock (_gate)
            {
                if (to >= _nextSequence)
                    Refresh();

                if (sessionId != 0 && sessionId != SessionId)
                {
                    messages = new List<SequencedPayload>();
                    return JournalRangeResult.WrongSession;
                }

                var index = FindFloor(from);
                if (index < 0)
                    return JournalReader.TryReadRange(_directory, SessionId, from, to, out messages);

                entry = _entries[index];
                segments = _segments;
                indexedSession = SessionId;
            }

            var result = JournalReader.TryReadRangeFrom(segments, entry.Segment, entry.Offset,
                indexedSession, entry.Sequence, from, to, out messages);

            if (result != JournalRangeResult.Success)
                messages.Clear();

            return result;
        }

        private void Build()
        {
            _entries.Clear();
            _segments = WriteAheadJournal.SegmentFiles(_directory);
            _scanSegment = -1;
            _scanOffset = 0;
            _sequencedRecords = 0;

            var report = JournalReader.Recover(_directory, (in JournalRecordView record) =>
            {
                if (record.Type == JournalRecordType.SegmentHeader)
                {
                    _scanSegment++;
                    _scanOffset = record.TotalSize;
                    return true;
                }

                if (TryGetFirstSequence(record, out var sequence))
                {
                    if (_sequencedRecords % _stride == 0)
                        _entries.Add(new IndexEntry(sequence, _scanSegment, _scanOffset));
                    _sequencedRecords++;
                }

                _scanOffset += record.TotalSize;
                return true;
            });

            if (report.Outcome == RecoveryOutcome.Corrupt || report.SessionId == 0)
                throw new InvalidDataException("Cannot index an invalid journal.");

            SessionId = report.SessionId;
            _nextSequence = report.NextSequence;
        }

        private void Refresh()
        {
            var current = WriteAheadJournal.SegmentFiles(_directory);

            if (!HasStablePrefix(current))
            {
                Build();
                return;
            }

            _segments = current;

            while (true)
            {
                if (_scanSegment < 0)
                {
                    if (!TryOpenNextSegment())
                        return;
                }

                using (var stream = new FileStream(_segments[_scanSegment], FileMode.Open,
                           FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1,
                           FileOptions.SequentialScan))
                {
                    var limit = stream.Length;
                    if (_scanOffset > limit)
                        throw new InvalidDataException("Journal segment shrank under the index.");
                    stream.Position = _scanOffset;
                    var reader = new JournalReader.PooledReader(stream, limit);

                    try
                    {
                        while (reader.Position < limit)
                        {
                            var offset = reader.Position;
                            var result = JournalReader.ReadRecord(ref reader, out var rented,
                                out var size);

                            if (result == JournalReadResult.Incomplete)
                                return;
                            if (result != JournalReadResult.Ok)
                                throw new InvalidDataException(
                                    $"Journal tail failed validation: {result}.");

                            try
                            {
                                JournalRecord.TryRead(rented.AsSpan(0, size), out var record);
                                if (!Advance(record, offset))
                                    throw new InvalidDataException(
                                        "Journal tail broke sequence continuity.");
                                _scanOffset = reader.Position;
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

                if (_scanSegment + 1 >= _segments.Count || !TryOpenNextSegment())
                    return;
            }
        }

        private bool TryOpenNextSegment()
        {
            var next = _scanSegment + 1;
            if (next >= _segments.Count)
                return false;

            using var stream = new FileStream(_segments[next], FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, bufferSize: 1,
                FileOptions.SequentialScan);
            var result = JournalReader.ReadRecord(stream, stream.Length, out var rented, out var size);

            if (result == JournalReadResult.Incomplete)
                return false;
            if (result != JournalReadResult.Ok)
                throw new InvalidDataException($"Journal segment header failed validation: {result}.");

            try
            {
                JournalRecord.TryRead(rented.AsSpan(0, size), out var record);
                if (record.Type != JournalRecordType.SegmentHeader || record.Payload.Length != 16 ||
                    System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(record.Payload) !=
                        SessionId ||
                    System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(
                        record.Payload.Slice(8)) != _nextSequence)
                    throw new InvalidDataException("Journal segment header is discontinuous.");

                if (_scanSegment >= 0 && WriteAheadJournal.IndexOf(_segments[next]) !=
                    WriteAheadJournal.IndexOf(_segments[_scanSegment]) + 1)
                    throw new InvalidDataException("Journal segment index is discontinuous.");

                _scanSegment = next;
                _scanOffset = size;
                return true;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        private bool Advance(in JournalRecordView record, long offset)
        {
            if (record.Type is JournalRecordType.Checkpoint or JournalRecordType.Audit)
                return record.Sequence == Sequencer.None || record.Sequence < _nextSequence;
            if (record.Type == JournalRecordType.FeedPacket &&
                (!FeedProtocol.TryReadHeader(record.Payload, out var header, out _) ||
                 header.SessionId != SessionId))
                return false;
            if (!TryGetRange(record, out var first, out var last) || first != _nextSequence)
                return false;

            if (_sequencedRecords % _stride == 0)
                _entries.Add(new IndexEntry(first, _scanSegment, offset));
            _sequencedRecords++;
            _nextSequence = last == ulong.MaxValue ? ulong.MaxValue : last + 1;
            return true;
        }

        private bool HasStablePrefix(List<string> current)
        {
            if (current.Count < _segments.Count)
                return false;

            for (var i = 0; i < _segments.Count; i++)
                if (!string.Equals(current[i], _segments[i], StringComparison.Ordinal))
                    return false;

            return true;
        }

        private int FindFloor(ulong sequence)
        {
            var low = 0;
            var high = _entries.Count - 1;
            var found = -1;

            while (low <= high)
            {
                var middle = low + ((high - low) >> 1);
                if (_entries[middle].Sequence <= sequence)
                {
                    found = middle;
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }

            return found;
        }

        private static bool TryGetFirstSequence(in JournalRecordView record, out ulong sequence)
        {
            sequence = record.Sequence;
            return record.Type is JournalRecordType.Message or JournalRecordType.FeedPacket;
        }

        private static bool TryGetRange(in JournalRecordView record, out ulong first, out ulong last)
        {
            first = record.Sequence;
            last = record.Sequence;

            if (record.Type == JournalRecordType.Message)
                return first != ulong.MaxValue;
            if (record.Type != JournalRecordType.FeedPacket ||
                !FeedProtocol.TryReadHeader(record.Payload, out var header, out _))
                return false;

            first = header.FirstSequence;
            last = first + header.MessageCount - 1;
            return true;
        }

        private readonly record struct IndexEntry(ulong Sequence, int Segment, long Offset);
    }
}
