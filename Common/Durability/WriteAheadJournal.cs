using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using MarketData.Common.Feed;

namespace MarketData.Common.Durability
{
    public enum DurabilityPolicy
    {
        OsBuffered,
        SyncEachRecord,
        SyncPeriodic,
    }

    /// <summary>Single-writer, segmented WAL with strict sequence and crash-tail recovery.</summary>
    public sealed class WriteAheadJournal : IDisposable
    {
        public static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(200);

        private const string SegmentPrefix = "segment-";
        private const string SegmentSuffix = ".jrn";
        private const string WriterLeaseName = "writer.lock";
        private const int StackRecordLimit = 2048;
        private static readonly int SegmentHeaderBytes = JournalRecord.SizeFor(16);
        private static readonly ConcurrentDictionary<string, byte> ActiveWriters =
            new(StringComparer.Ordinal);

        private readonly object _gate = new();
        private readonly string _directory;
        private readonly long _segmentBytes;
        private readonly ulong _initialSequence;
        private readonly string _leaseKey;
        private readonly FileStream _writerLease;
        private readonly ManualResetEventSlim _syncShutdown;
        private readonly Thread _syncThread;

        private FileStream _segment;
        private long _segmentLength;
        private int _segmentIndex;
        private ulong _nextSequence;
        private long _recordsAppended;
        private long _syncs;
        private Exception _failure;
        private bool _hasSequencedRecords;
        private bool _dirty;
        private bool _disposed;

        public WriteAheadJournal(string directory, ulong sessionId,
            DurabilityPolicy policy = DurabilityPolicy.SyncPeriodic,
            long segmentBytes = 64L * 1024 * 1024,
            TimeSpan? syncInterval = null,
            ulong initialSequence = 1)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(directory);
            if (sessionId == 0)
                throw new ArgumentOutOfRangeException(nameof(sessionId));
            if (!Enum.IsDefined(policy))
                throw new ArgumentOutOfRangeException(nameof(policy));
            if (segmentBytes < SegmentHeaderBytes + JournalRecord.SizeFor(JournalRecord.MaxPayloadSize))
                throw new ArgumentOutOfRangeException(nameof(segmentBytes));

            var interval = syncInterval ?? FlushInterval;
            if (interval <= TimeSpan.Zero || interval.TotalMilliseconds > uint.MaxValue - 1)
                throw new ArgumentOutOfRangeException(nameof(syncInterval));

            _directory = Path.GetFullPath(directory);
            _leaseKey = OperatingSystem.IsWindows() ? _directory.ToUpperInvariant() : _directory;
            _segmentBytes = segmentBytes;
            _initialSequence = initialSequence;
            _nextSequence = initialSequence;
            SessionId = sessionId;
            Policy = policy;
            SyncInterval = interval;

            Directory.CreateDirectory(_directory);

            if (!ActiveWriters.TryAdd(_leaseKey, 0))
                throw new IOException("The journal already has a writer in this process.");

            try
            {
                _writerLease = new FileStream(Path.Combine(_directory, WriterLeaseName),
                    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 1);
                OpenOrRecover();

                if (policy == DurabilityPolicy.SyncPeriodic)
                {
                    _syncShutdown = new ManualResetEventSlim(false);
                    _syncThread = new Thread(SyncLoop)
                    {
                        IsBackground = true,
                        Name = $"journal-sync-{sessionId:X16}",
                    };
                    _syncThread.Start();
                }
            }
            catch
            {
                _segment?.Dispose();
                _writerLease?.Dispose();
                _syncShutdown?.Dispose();
                ActiveWriters.TryRemove(_leaseKey, out _);
                throw;
            }
        }

        public DurabilityPolicy Policy { get; }
        public TimeSpan SyncInterval { get; }
        public ulong SessionId { get; }
        public string DirectoryPath => _directory;

        public ulong LastSequence
        {
            get { lock (_gate) return _hasSequencedRecords ? _nextSequence - 1 : Sequencer.None; }
        }

        public ulong NextSequence
        {
            get { lock (_gate) return _nextSequence; }
        }

        public bool HasSequencedRecords
        {
            get { lock (_gate) return _hasSequencedRecords; }
        }

        public long RecordsAppended => Interlocked.Read(ref _recordsAppended);
        public long Syncs => Interlocked.Read(ref _syncs);

        public ulong Append(JournalRecordType type, ulong sequence, long timestamp,
            ReadOnlySpan<byte> payload)
        {
            if (type is JournalRecordType.Invalid or JournalRecordType.SegmentHeader or
                JournalRecordType.FeedPacket || type > JournalRecordType.FeedPacket)
                throw new ArgumentOutOfRangeException(nameof(type));

            return AppendCore(type, sequence, timestamp, payload, 1);
        }

        public ulong AppendNext(long timestamp, ReadOnlySpan<byte> payload)
        {
            lock (_gate)
            {
                EnsureWritable();
                return AppendLocked(JournalRecordType.Message, _nextSequence, timestamp, payload, 1);
            }
        }

        /// <summary>Persists one sealed feed packet before multicast publication.</summary>
        public ulong AppendPacket(ReadOnlySpan<byte> packet)
        {
            if (!FeedProtocol.TryReadHeader(packet, out var header, out var error))
                throw new InvalidDataException($"Invalid feed packet: {error}.");
            if (header.SessionId != SessionId)
                throw new InvalidDataException("Feed and journal sessions differ.");

            return AppendCore(JournalRecordType.FeedPacket, header.FirstSequence,
                header.SourceTimestamp, packet, header.MessageCount);
        }

        public void Sync()
        {
            lock (_gate)
            {
                EnsureWritable();
                ForceToDevice();
            }
        }

        private ulong AppendCore(JournalRecordType type, ulong sequence, long timestamp,
            ReadOnlySpan<byte> payload, ushort sequenceCount)
        {
            lock (_gate)
            {
                EnsureWritable();
                return AppendLocked(type, sequence, timestamp, payload, sequenceCount);
            }
        }

        private ulong AppendLocked(JournalRecordType type, ulong sequence, long timestamp,
            ReadOnlySpan<byte> payload, ushort sequenceCount)
        {
            ValidateSequence(type, sequence, sequenceCount, payload);

            var size = JournalRecord.SizeFor(payload.Length);
            byte[] rented = null;
            Span<byte> buffer = size <= StackRecordLimit
                ? stackalloc byte[size]
                : (rented = ArrayPool<byte>.Shared.Rent(size));

            try
            {
                JournalRecord.Write(buffer, type, sequence, timestamp, payload);

                if (_segmentLength + size > _segmentBytes)
                    RollSegment();

                _segment.Write(buffer.Slice(0, size));
                _segmentLength += size;
                _dirty = true;
                Interlocked.Increment(ref _recordsAppended);

                if (type is JournalRecordType.Message or JournalRecordType.FeedPacket)
                {
                    _nextSequence = checked(sequence + sequenceCount);
                    _hasSequencedRecords = true;
                }

                ApplyDurabilityPolicy();
                return sequence;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                _failure ??= error;
                throw;
            }
            finally
            {
                if (rented is not null)
                    ArrayPool<byte>.Shared.Return(rented);
            }
        }

        private void ValidateSequence(JournalRecordType type, ulong sequence, ushort sequenceCount,
            ReadOnlySpan<byte> payload)
        {
            switch (type)
            {
                case JournalRecordType.Message:
                case JournalRecordType.FeedPacket:
                    if (sequence != _nextSequence)
                        throw new InvalidOperationException(
                            $"Expected sequence {_nextSequence}, received {sequence}.");
                    if (sequenceCount == 0 || sequence > ulong.MaxValue - sequenceCount)
                        throw new OverflowException("The journal sequence space is exhausted.");
                    break;

                case JournalRecordType.Checkpoint:
                    if (payload.Length != sizeof(ulong) ||
                        BinaryPrimitives.ReadUInt64LittleEndian(payload) != sequence ||
                        (_hasSequencedRecords ? sequence > _nextSequence - 1 : sequence != Sequencer.None))
                        throw new InvalidDataException("Checkpoint marker is outside the durable prefix.");
                    break;

                case JournalRecordType.Audit:
                    if (_hasSequencedRecords ? sequence > _nextSequence - 1 : sequence != Sequencer.None)
                        throw new InvalidDataException("Audit record is outside the durable prefix.");
                    break;
            }
        }

        private void ApplyDurabilityPolicy()
        {
            if (Policy == DurabilityPolicy.SyncEachRecord)
                ForceToDevice();
        }

        private void SyncLoop()
        {
            while (!_syncShutdown.Wait(SyncInterval))
            {
                try
                {
                    lock (_gate)
                    {
                        if (_disposed || _failure is not null)
                            return;
                        if (_dirty)
                            ForceToDevice();
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    lock (_gate)
                        _failure ??= error;

                    return;
                }
            }
        }

        private void ForceToDevice()
        {
            if (!_dirty)
                return;

            _segment.Flush(flushToDisk: true);
            _dirty = false;
            Interlocked.Increment(ref _syncs);
        }

        private void RollSegment()
        {
            ForceToDevice();
            _segment.Dispose();

            if (_segmentIndex == int.MaxValue)
                throw new OverflowException("The segment index is exhausted.");

            _segmentIndex++;
            OpenNewSegment();
        }

        private void OpenOrRecover()
        {
            var segments = SegmentFiles(_directory);

            if (segments.Count == 0)
            {
                _segmentIndex = 0;
                OpenNewSegment();
                return;
            }

            var report = JournalReader.Recover(_directory, expectedSessionId: SessionId,
                expectedInitialSequence: _initialSequence);

            if (report.Outcome == RecoveryOutcome.Corrupt)
                throw new InvalidDataException(
                    $"Journal recovery failed in {report.DamagedSegment}: {report.Failure}.");

            if (report.Outcome == RecoveryOutcome.TruncatedTail)
            {
                RepairTail(report);
                report = JournalReader.Recover(_directory, expectedSessionId: SessionId,
                    expectedInitialSequence: _initialSequence);

                if (report.Outcome != RecoveryOutcome.Clean)
                    throw new InvalidDataException("The journal tail could not be repaired.");

                segments = SegmentFiles(_directory);
            }

            if (!report.HasSequencedRecords && report.NextSequence != _initialSequence)
                throw new InvalidDataException("The journal initial sequence does not match.");

            _nextSequence = report.NextSequence;
            _hasSequencedRecords = report.HasSequencedRecords;

            if (segments.Count == 0)
            {
                _segmentIndex = 0;
                OpenNewSegment();
                return;
            }

            _segmentIndex = IndexOf(segments[^1]);
            _segment = OpenSegment(segments[^1], FileMode.Open);
            _segmentLength = _segment.Length;
            _segment.Position = _segmentLength;
        }

        private static void RepairTail(RecoveryReport report)
        {
            if (report.ValidBytesInDamagedSegment == 0)
            {
                File.Delete(report.DamagedSegment);
                return;
            }

            using var stream = new FileStream(report.DamagedSegment, FileMode.Open, FileAccess.Write,
                FileShare.Read, bufferSize: 1);
            stream.SetLength(report.ValidBytesInDamagedSegment);
            stream.Flush(flushToDisk: true);
        }

        private void OpenNewSegment()
        {
            var path = Path.Combine(_directory, $"{SegmentPrefix}{_segmentIndex:D9}{SegmentSuffix}");
            _segment = OpenSegment(path, FileMode.CreateNew);
            _segmentLength = 0;

            Span<byte> payload = stackalloc byte[16];
            BinaryPrimitives.WriteUInt64LittleEndian(payload, SessionId);
            BinaryPrimitives.WriteUInt64LittleEndian(payload.Slice(8), _nextSequence);

            var size = JournalRecord.SizeFor(payload.Length);
            Span<byte> record = stackalloc byte[size];
            JournalRecord.Write(record, JournalRecordType.SegmentHeader, _nextSequence,
                DateTime.UtcNow.Ticks, payload);
            _segment.Write(record);
            _segmentLength = size;
            _dirty = true;
        }

        private static FileStream OpenSegment(string path, FileMode mode)
            => new(path, mode, FileAccess.Write, FileShare.ReadWrite, bufferSize: 1,
                FileOptions.SequentialScan);

        private void EnsureWritable()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_failure is not null)
                throw new IOException("The journal is fail-stopped after an I/O error.", _failure);
        }

        internal static List<string> SegmentFiles(string directory)
            => Directory.Exists(directory)
                ? Directory.GetFiles(directory, SegmentPrefix + "*" + SegmentSuffix)
                    .OrderBy(path => path, StringComparer.Ordinal).ToList()
                : new List<string>();

        internal static int IndexOf(string path)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            return name.StartsWith(SegmentPrefix, StringComparison.Ordinal) &&
                   int.TryParse(name.AsSpan(SegmentPrefix.Length), out var index)
                ? index
                : throw new InvalidDataException($"Invalid segment name: {path}.");
        }

        public void Dispose()
        {
            var stopSync = false;

            try
            {
                lock (_gate)
                {
                    if (_disposed)
                        return;

                    stopSync = true;
                    _syncShutdown?.Set();

                    try
                    {
                        if (_failure is null)
                            ForceToDevice();
                    }
                    finally
                    {
                        _disposed = true;
                        _segment?.Dispose();
                        _writerLease.Dispose();
                        ActiveWriters.TryRemove(_leaseKey, out _);
                    }
                }
            }
            finally
            {
                if (stopSync)
                {
                    _syncThread?.Join();
                    _syncShutdown?.Dispose();
                }
            }
        }
    }
}
