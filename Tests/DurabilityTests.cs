using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MarketData.Common.Books;
using MarketData.Common.Durability;
using MarketData.Common.Feed;
using Xunit;

namespace MarketData.Tests
{
    /// <summary>Durability, corruption, restart, and gap-fill invariants.</summary>
    public sealed class DurabilityTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(),
            "mds-durability-" + Guid.NewGuid().ToString("N"));

        private const ulong Session = 0x5345535331UL;

        public DurabilityTests() => Directory.CreateDirectory(_root);

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        }

        private string Dir(string name)
        {
            var path = Path.Combine(_root, name);
            Directory.CreateDirectory(path);
            return path;
        }

        private static byte[] Payload(int n) => Encoding.UTF8.GetBytes($"message-{n:D6}");

        private static byte[] SnapshotPacket(ulong sequence, ulong session = Session)
        {
            var packet = new byte[FeedProtocol.HeaderSize + FeedProtocol.SnapshotSize(0, 0)];
            var size = FeedProtocol.HeaderSize + FeedProtocol.WriteSnapshot(
                packet.AsSpan(FeedProtocol.HeaderSize), 1, ReadOnlySpan<PriceLevel>.Empty,
                ReadOnlySpan<PriceLevel>.Empty);
            FeedProtocol.WriteHeader(packet.AsSpan(0, size), 1, session, sequence, 1);
            return packet.AsSpan(0, size).ToArray();
        }

        private static byte[] IncrementalPacket(ulong sequence, int price, ulong session = Session)
        {
            var packet = new byte[FeedProtocol.HeaderSize + FeedProtocol.IncrementalSize];
            FeedProtocol.WriteIncremental(packet.AsSpan(FeedProtocol.HeaderSize), FeedMessageType.Add,
                1, Side.Bid, new PriceLevel(price, 100));
            FeedProtocol.WriteHeader(packet, 1, session, sequence, 1);
            return packet;
        }

        // ------------------------------------------------------------------ framing

        [Fact]
        public void ARecordRoundTrips()
        {
            var payload = Payload(1);
            var buffer = new byte[JournalRecord.SizeFor(payload.Length)];

            JournalRecord.Write(buffer, JournalRecordType.Message, 42, 12345, payload);

            Assert.Equal(JournalReadResult.Ok, JournalRecord.TryRead(buffer, out var record));
            Assert.Equal(JournalRecordType.Message, record.Type);
            Assert.Equal(42UL, record.Sequence);
            Assert.Equal(12345, record.Timestamp);
            Assert.Equal(payload, record.Payload.ToArray());
            Assert.Equal(buffer.Length, record.TotalSize);
        }

        /// <summary>Every truncated prefix is incomplete.</summary>
        [Fact]
        public void EveryTruncationIsDetected()
        {
            var payload = Payload(7);
            var buffer = new byte[JournalRecord.SizeFor(payload.Length)];
            JournalRecord.Write(buffer, JournalRecordType.Message, 9, 1, payload);

            for (var length = 0; length < buffer.Length; length++)
            {
                var result = JournalRecord.TryRead(buffer.AsSpan(0, length), out _);

                Assert.True(result != JournalReadResult.Ok,
                    $"a {length}-byte prefix of a {buffer.Length}-byte record validated as complete");
            }
        }

        /// <summary>Every single-bit flip anywhere in a record must be caught.</summary>
        [Fact]
        public void EverySingleBitFlipIsDetected()
        {
            var payload = Payload(3);
            var original = new byte[JournalRecord.SizeFor(payload.Length)];
            JournalRecord.Write(original, JournalRecordType.Message, 5, 99, payload);

            var missed = new List<string>();

            for (var index = 0; index < original.Length; index++)
            {
                for (var bit = 0; bit < 8; bit++)
                {
                    var corrupted = (byte[])original.Clone();
                    corrupted[index] ^= (byte)(1 << bit);

                    if (JournalRecord.TryRead(corrupted, out _) == JournalReadResult.Ok)
                        missed.Add($"byte {index} bit {bit}");
                }
            }

            Assert.True(missed.Count == 0,
                $"{missed.Count} single-bit corruptions validated as clean: {string.Join(", ", missed.Take(8))}");
        }

        // ------------------------------------------------------------------ sequencer

        [Fact]
        public void SequencesStartAtOneAndNeverRepeat()
        {
            var sequencer = new Sequencer();

            Assert.Equal(Sequencer.None, sequencer.Last);
            Assert.Equal(1UL, sequencer.Next());
            Assert.Equal(2UL, sequencer.Next());

            var first = sequencer.Reserve(5);
            Assert.Equal(3UL, first);
            Assert.Equal(7UL, sequencer.Last);
            Assert.Equal(8UL, sequencer.Next());
        }

        [Fact]
        public void ConcurrentProducersNeverShareASequence()
        {
            var sequencer = new Sequencer();
            const int threads = 8;
            const int each = 10_000;

            var claimed = new ulong[threads][];

            Parallel.For(0, threads, t =>
            {
                var mine = new ulong[each];
                for (var i = 0; i < each; i++)
                    mine[i] = sequencer.Next();
                claimed[t] = mine;
            });

            var all = claimed.SelectMany(x => x).ToList();

            Assert.Equal(threads * each, all.Count);
            Assert.Equal(threads * each, all.Distinct().Count());
            Assert.Equal(1UL, all.Min());
            Assert.Equal((ulong)(threads * each), all.Max());
        }

        /// <summary>Rewinding would re-issue numbers subscribers have already applied.</summary>
        [Fact]
        public void TheSequencerRefusesToRewind()
        {
            var sequencer = new Sequencer();
            sequencer.Reserve(100);

            sequencer.ResumeFrom(100);   // same point is fine
            sequencer.ResumeFrom(150);   // forward is fine

            Assert.Throws<ArgumentOutOfRangeException>(() => sequencer.ResumeFrom(149));
        }

        // ------------------------------------------------------------------ journal

        [Fact]
        public void AJournalRecoversEverythingItAcknowledged()
        {
            var directory = Dir("clean");
            const int count = 500;

            using (var journal = new WriteAheadJournal(directory, Session, DurabilityPolicy.SyncEachRecord))
            {
                for (var i = 1; i <= count; i++)
                    journal.Append(JournalRecordType.Message, (ulong)i, i, Payload(i));
            }

            var seen = new List<ulong>();
            var report = JournalReader.Recover(directory, (in JournalRecordView record) =>
            {
                if (record.Type == JournalRecordType.Message)
                    seen.Add(record.Sequence);
                return true;
            });

            Assert.Equal(RecoveryOutcome.Clean, report.Outcome);
            Assert.Equal((ulong)count, report.LastSequence);
            Assert.Equal(Enumerable.Range(1, count).Select(i => (ulong)i), seen);
        }

        /// <summary>
        /// A process killed mid-append leaves a partial record; recovery must keep everything
        /// before it and say so, rather than failing or silently inventing.
        /// </summary>
        [Fact]
        public void ATornTailIsRecoverableAndReported()
        {
            var directory = Dir("torn");
            const int count = 200;

            using (var journal = new WriteAheadJournal(directory, Session, DurabilityPolicy.SyncEachRecord))
            {
                for (var i = 1; i <= count; i++)
                    journal.Append(JournalRecordType.Message, (ulong)i, i, Payload(i));
            }

            // Simulate the kill: chop the last record in half.
            var segment = Directory.GetFiles(directory, "segment-*.jrn").OrderBy(f => f).Last();
            var bytes = File.ReadAllBytes(segment);
            var recordSize = JournalRecord.SizeFor(Payload(count).Length);
            File.WriteAllBytes(segment, bytes.AsSpan(0, bytes.Length - recordSize / 2).ToArray());

            var seen = new List<ulong>();
            var report = JournalReader.Recover(directory, (in JournalRecordView record) =>
            {
                if (record.Type == JournalRecordType.Message)
                    seen.Add(record.Sequence);
                return true;
            });

            Assert.Equal(RecoveryOutcome.TruncatedTail, report.Outcome);
            Assert.True(report.Resumable);
            Assert.Equal((ulong)(count - 1), report.LastSequence);
            Assert.Equal(Enumerable.Range(1, count - 1).Select(i => (ulong)i), seen);
        }

        /// <summary>
        /// Damage in the middle of the log is not a torn tail and must not be reported as one.
        /// </summary>
        [Fact]
        public void CorruptionInTheMiddleIsNotMistakenForATornTail()
        {
            var directory = Dir("corrupt");

            using (var journal = new WriteAheadJournal(directory, Session, DurabilityPolicy.SyncEachRecord))
            {
                for (var i = 1; i <= 100; i++)
                    journal.Append(JournalRecordType.Message, (ulong)i, i, Payload(i));
            }

            var segment = Directory.GetFiles(directory, "segment-*.jrn").OrderBy(f => f).Last();
            var bytes = File.ReadAllBytes(segment);
            bytes[bytes.Length / 2] ^= 0xFF;         // flip a byte in the middle
            File.WriteAllBytes(segment, bytes);

            var report = JournalReader.Recover(directory);

            Assert.Equal(RecoveryOutcome.Corrupt, report.Outcome);
            Assert.False(report.Resumable);
            Assert.Equal(segment, report.DamagedSegment);
        }

        [Fact]
        public void SegmentsRotateAndAllOfThemAreRead()
        {
            var directory = Dir("segments");
            var payload = Payload(1);
            var perSegment = 20;
            var segmentBytes = JournalRecord.SizeFor(16) +
                JournalRecord.SizeFor(JournalRecord.MaxPayloadSize);

            using (var journal = new WriteAheadJournal(directory, Session,
                       DurabilityPolicy.OsBuffered, segmentBytes))
            {
                for (var i = 1; i <= perSegment * 5; i++)
                    journal.Append(JournalRecordType.Message, (ulong)i, i, payload);
            }

            var report = JournalReader.Recover(directory);

            Assert.Equal(RecoveryOutcome.Clean, report.Outcome);
            Assert.Equal((ulong)(perSegment * 5), report.LastSequence);
        }

        [Theory]
        [InlineData(DurabilityPolicy.OsBuffered)]
        [InlineData(DurabilityPolicy.SyncEachRecord)]
        [InlineData(DurabilityPolicy.SyncPeriodic)]
        public void EveryDurabilityPolicyRecoversWhatItReturnedFrom(DurabilityPolicy policy)
        {
            var directory = Dir("policy-" + policy);

            using (var journal = new WriteAheadJournal(directory, Session, policy))
            {
                for (var i = 1; i <= 250; i++)
                    journal.Append(JournalRecordType.Message, (ulong)i, i, Payload(i));
            }

            // Dispose forces the device, so a clean shutdown is lossless under every policy.
            // The policies differ only in what an *unclean* stop loses.
            var report = JournalReader.Recover(directory);

            Assert.Equal(RecoveryOutcome.Clean, report.Outcome);
            Assert.Equal(250UL, report.LastSequence);
        }

        // ------------------------------------------------------------------ checkpoints

        /// <summary>Checkpoint plus tail replay equals full replay.</summary>
        [Fact]
        public void ACheckpointPlusTheRestOfTheLogEqualsAFullReplay()
        {
            var directory = Dir("checkpoint-journal");
            var checkpoints = Dir("checkpoint-state");

            var random = new Random(20260819);
            var operations = new List<(int Instrument, Side Side, int Price, uint Quantity)>();

            for (var i = 0; i < 2_000; i++)
            {
                operations.Add((
                    random.Next(1, 4),
                    random.Next(2) == 0 ? Side.Bid : Side.Ask,
                    random.Next(-50, 51),
                    (uint)random.Next(0, 500)));
            }

            IOrderBook Make(int _) => new SortedArrayBook(16);

            var live = new Dictionary<int, IOrderBook>();
            ulong checkpointAt = 0;

            using (var journal = new WriteAheadJournal(directory, Session, DurabilityPolicy.OsBuffered))
            {
                var encoded = new byte[13];

                for (var i = 0; i < operations.Count; i++)
                {
                    var op = operations[i];
                    var sequence = (ulong)(i + 1);

                    if (!live.TryGetValue(op.Instrument, out var book))
                        live[op.Instrument] = book = Make(op.Instrument);

                    book.Upsert(op.Side, op.Price, op.Quantity);

                    System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(encoded, op.Instrument);
                    encoded[4] = (byte)op.Side;
                    System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(encoded.AsSpan(5), op.Price);
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(encoded.AsSpan(9), op.Quantity);

                    journal.Append(JournalRecordType.Message, sequence, i, encoded);

                    // Checkpoint halfway through.
                    if (i == operations.Count / 2)
                    {
                        checkpointAt = sequence;
                        Checkpoint.Write(checkpoints, journal, sequence, Session, live);
                    }
                }
            }

            // Rebuild A: from the checkpoint, then the tail of the log.
            var restored = new Dictionary<int, IOrderBook>();
            var path = Checkpoint.FindLatest(checkpoints);
            Assert.NotNull(path);

            var restoredAt = Checkpoint.Restore(path, Make, restored);
            Assert.Equal(checkpointAt, restoredAt);

            JournalReader.Recover(directory, (in JournalRecordView record) =>
            {
                if (record.Type == JournalRecordType.Message && record.Sequence > restoredAt)
                    Apply(restored, Make, record.Payload);
                return true;
            });

            // Rebuild B: the entire log from nothing.
            var replayed = new Dictionary<int, IOrderBook>();

            JournalReader.Recover(directory, (in JournalRecordView record) =>
            {
                if (record.Type == JournalRecordType.Message)
                    Apply(replayed, Make, record.Payload);
                return true;
            });

            AssertSameBooks(replayed, restored);
            AssertSameBooks(live, restored);
        }

        private static void Apply(IDictionary<int, IOrderBook> books, Func<int, IOrderBook> make,
            ReadOnlySpan<byte> encoded)
        {
            var instrument = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(encoded);
            var side = (Side)encoded[4];
            var price = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(encoded.Slice(5));
            var quantity = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(encoded.Slice(9));

            if (!books.TryGetValue(instrument, out var book))
                books[instrument] = book = make(instrument);

            book.Upsert(side, price, quantity);
        }

        private static void AssertSameBooks(IDictionary<int, IOrderBook> expected,
            IDictionary<int, IOrderBook> actual)
        {
            Assert.Equal(expected.Keys.OrderBy(k => k), actual.Keys.OrderBy(k => k));

            foreach (var (instrument, book) in expected)
            {
                foreach (var side in new[] { Side.Bid, Side.Ask })
                {
                    var left = new PriceLevel[book.Count(side)];
                    var right = new PriceLevel[actual[instrument].Count(side)];

                    book.CopyTo(side, left);
                    actual[instrument].CopyTo(side, right);

                    Assert.True(left.SequenceEqual(right),
                        $"instrument {instrument} {side} diverged between replay and checkpoint restore");
                }
            }
        }

        [Fact]
        public void PruningKeepsTheNewestCheckpointsAndNeverAllOfThem()
        {
            var directory = Dir("checkpoint-journal-prune");
            var checkpoints = Dir("checkpoint-prune");

            using (var journal = new WriteAheadJournal(directory, Session, DurabilityPolicy.OsBuffered))
            {
                var books = new Dictionary<int, IOrderBook> { [1] = new SortedArrayBook(4) };
                books[1].Upsert(Side.Bid, 10, 100);

                for (ulong sequence = 1; sequence <= 6; sequence++)
                {
                    journal.Append(JournalRecordType.Message, sequence, 0, ReadOnlySpan<byte>.Empty);
                    Checkpoint.Write(checkpoints, journal, sequence, Session, books);
                }
            }

            Assert.Equal(3, Checkpoint.Prune(checkpoints, keep: 3));

            var remaining = Directory.GetFiles(checkpoints, "checkpoint-*.chk")
                .Select(Checkpoint.SequenceOf).OrderBy(s => s).ToArray();

            Assert.Equal(new ulong[] { 4, 5, 6 }, remaining);
            Assert.Throws<ArgumentOutOfRangeException>(() => Checkpoint.Prune(checkpoints, keep: 0));
        }

        /// <summary>Checkpoint recovery skips complete historical segments.</summary>
        [Fact]
        public void RecoveringFromACheckpointSkipsSegmentsBelowIt()
        {
            var directory = Dir("skip-journal");
            var payload = new byte[512];
            var segmentBytes = JournalRecord.SizeFor(16) +
                JournalRecord.SizeFor(JournalRecord.MaxPayloadSize);
            const int count = 4_000;

            using (var journal = new WriteAheadJournal(directory, Session, DurabilityPolicy.OsBuffered,
                       segmentBytes))
            {
                for (var i = 1; i <= count; i++)
                    journal.Append(JournalRecordType.Message, (ulong)i, i, payload);
            }

            // More than one segment, or the test proves nothing.
            var segments = Directory.GetFiles(directory, "segment-*.jrn").Length;
            Assert.True(segments > 1, $"expected the log to rotate; got {segments} segment(s)");

            var visitedFromStart = 0;
            JournalReader.Recover(directory, (in JournalRecordView _) => { visitedFromStart++; return true; });

            var late = (ulong)(count - 100);
            var visitedFromCheckpoint = 0;
            var lowestSeen = ulong.MaxValue;

            var report = JournalReader.Recover(directory, (in JournalRecordView record) =>
            {
                visitedFromCheckpoint++;

                if (record.Type == JournalRecordType.Message && record.Sequence < lowestSeen)
                    lowestSeen = record.Sequence;

                return true;
            }, late);

            Assert.Equal(RecoveryOutcome.Clean, report.Outcome);

            // Far fewer records visited, and none from the earliest part of the log.
            Assert.True(visitedFromCheckpoint < visitedFromStart / 2,
                $"scan visited {visitedFromCheckpoint} of {visitedFromStart} records; the skip did not happen");
            Assert.True(lowestSeen > 1,
                "the scan still reached sequence 1, so no segment was skipped");
        }

        /// <summary>Skipping must never drop a record at or after the requested point.</summary>
        [Fact]
        public void SegmentSkippingNeverLosesARecordInRange()
        {
            var directory = Dir("skip-correct");
            var payload = new byte[512];
            var segmentBytes = JournalRecord.SizeFor(16) +
                JournalRecord.SizeFor(JournalRecord.MaxPayloadSize);
            const int count = 3_000;

            using (var journal = new WriteAheadJournal(directory, Session, DurabilityPolicy.OsBuffered,
                       segmentBytes))
            {
                for (var i = 1; i <= count; i++)
                    journal.Append(JournalRecordType.Message, (ulong)i, i, payload);
            }

            foreach (var from in new ulong[] { 0, 1, 2, 500, 1500, 2999, 3000 })
            {
                var seen = new List<ulong>();

                JournalReader.Recover(directory, (in JournalRecordView record) =>
                {
                    if (record.Type == JournalRecordType.Message)
                        seen.Add(record.Sequence);
                    return true;
                }, from);

                var expected = Enumerable.Range(1, count)
                    .Select(i => (ulong)i)
                    .Where(s => s > from)
                    .ToList();

                // Everything at or after the requested point must be present. Records before it
                // may or may not appear depending on where segments happened to fall, which is
                // why the assertion is a superset check rather than equality.
                Assert.True(expected.All(seen.Contains),
                    $"from {from}: lost {expected.Count(s => !seen.Contains(s))} in-range record(s)");
            }
        }

        // ------------------------------------------------------------------ retransmission

        [Fact]
        public async Task AGapIsFilledFromTheJournal()
        {
            var directory = Dir("gapfill");

            using (var journal = new WriteAheadJournal(directory, Session, DurabilityPolicy.SyncEachRecord))
            {
                for (var i = 1; i <= 300; i++)
                    journal.Append(JournalRecordType.Message, (ulong)i, i, Payload(i));
            }

            using var service = new RetransmissionService(directory);
            service.Start();

            var client = new RetransmissionClient(service.Port);
            var recovered = await client.RequestAsync(100, 109,
                TestContext.Current.CancellationToken);

            Assert.NotNull(recovered);
            Assert.Equal(10, recovered.Count);
            Assert.Equal(Enumerable.Range(100, 10).Select(i => (ulong)i), recovered.Select(m => m.Sequence));

            for (var i = 0; i < recovered.Count; i++)
                Assert.Equal(Payload(100 + i), recovered[i].Payload);
        }

        /// <summary>Oversized recovery ranges require a snapshot.</summary>
        [Fact]
        public async Task AnOversizedRequestIsRefusedRatherThanServed()
        {
            var directory = Dir("gapfill-refuse");

            using (var journal = new WriteAheadJournal(directory, Session, DurabilityPolicy.OsBuffered))
                journal.Append(JournalRecordType.Message, 1, 0, Payload(1));

            using var service = new RetransmissionService(directory);
            service.Start();

            var client = new RetransmissionClient(service.Port);
            var refused = await client.RequestAsync(1, RetransmissionService.MaxRangeLength + 1,
                TestContext.Current.CancellationToken);

            Assert.Null(refused);
            Assert.Equal(1, service.RequestsRefused);
            Assert.Equal(0, service.RequestsServed);
        }

        [Fact]
        public async Task GapFillWorksWhileThePublisherIsStillWriting()
        {
            var directory = Dir("gapfill-live");

            using var journal = new WriteAheadJournal(directory, Session, DurabilityPolicy.SyncEachRecord);

            for (var i = 1; i <= 50; i++)
                journal.Append(JournalRecordType.Message, (ulong)i, i, Payload(i));

            using var service = new RetransmissionService(directory);
            service.Start();

            var publishing = Task.Run(() =>
            {
                for (var i = 51; i <= 400; i++)
                    journal.Append(JournalRecordType.Message, (ulong)i, i, Payload(i));
            }, TestContext.Current.CancellationToken);

            var client = new RetransmissionClient(service.Port);
            var recovered = await client.RequestAsync(10, 19,
                TestContext.Current.CancellationToken);

            await publishing;

            Assert.NotNull(recovered);
            Assert.Equal(10, recovered.Count);
            Assert.Equal(Payload(10), recovered[0].Payload);
        }

        [Fact]
        public void ReopeningResumesTheRecoveredWatermark()
        {
            var directory = Dir("resume");

            using (var journal = new WriteAheadJournal(directory, Session,
                       DurabilityPolicy.SyncEachRecord))
            {
                for (ulong sequence = 1; sequence <= 10; sequence++)
                    journal.Append(JournalRecordType.Message, sequence, 0, Payload((int)sequence));
            }

            using (var journal = new WriteAheadJournal(directory, Session,
                       DurabilityPolicy.SyncEachRecord))
            {
                Assert.Equal(11UL, journal.NextSequence);
                Assert.Equal(10UL, journal.LastSequence);
                Assert.Throws<InvalidOperationException>(() =>
                    journal.Append(JournalRecordType.Message, 10, 0, Payload(10)));
                journal.Append(JournalRecordType.Message, 11, 0, Payload(11));
            }

            var report = JournalReader.Recover(directory);
            Assert.Equal(RecoveryOutcome.Clean, report.Outcome);
            Assert.Equal(11UL, report.LastSequence);
            Assert.Equal(12UL, report.NextSequence);
        }

        [Fact]
        public void ReopeningRefusesADifferentInitialSequence()
        {
            var directory = Dir("resume-initial");

            using (var journal = new WriteAheadJournal(directory, Session,
                       DurabilityPolicy.SyncEachRecord, initialSequence: 1))
                journal.Append(JournalRecordType.Message, 1, 0, Payload(1));

            Assert.Throws<InvalidDataException>(() => new WriteAheadJournal(directory, Session,
                DurabilityPolicy.SyncEachRecord, initialSequence: 0));
        }

        [Fact]
        public void ReopeningReplacesAnIncompleteFirstSegment()
        {
            var directory = Dir("repair-first-header");

            using (new WriteAheadJournal(directory, Session, DurabilityPolicy.SyncEachRecord)) { }

            var segment = Directory.GetFiles(directory, "segment-*.jrn").Single();
            using (var stream = new FileStream(segment, FileMode.Open, FileAccess.Write))
                stream.SetLength(4);

            using (var journal = new WriteAheadJournal(directory, Session,
                       DurabilityPolicy.SyncEachRecord))
            {
                Assert.Equal(1UL, journal.NextSequence);
                journal.AppendNext(0, Payload(1));
            }

            var report = JournalReader.Recover(directory);
            Assert.Equal(RecoveryOutcome.Clean, report.Outcome);
            Assert.Equal(1UL, report.LastSequence);
        }

        [Fact]
        public void ReopeningRepairsATornTailBeforeAppending()
        {
            var directory = Dir("repair-tail");

            using (var journal = new WriteAheadJournal(directory, Session,
                       DurabilityPolicy.SyncEachRecord))
            {
                for (ulong sequence = 1; sequence <= 10; sequence++)
                    journal.Append(JournalRecordType.Message, sequence, 0, Payload((int)sequence));
            }

            var segment = Directory.GetFiles(directory, "segment-*.jrn").Single();
            using (var stream = new FileStream(segment, FileMode.Open, FileAccess.Write))
                stream.SetLength(stream.Length - JournalRecord.SizeFor(Payload(10).Length) / 2);

            using (var journal = new WriteAheadJournal(directory, Session,
                       DurabilityPolicy.SyncEachRecord))
            {
                Assert.Equal(10UL, journal.NextSequence);
                journal.Append(JournalRecordType.Message, 10, 0, Payload(10));
            }

            var report = JournalReader.Recover(directory);
            Assert.Equal(RecoveryOutcome.Clean, report.Outcome);
            Assert.Equal(10UL, report.LastSequence);
        }

        [Fact]
        public void ReopeningRefusesCommittedCorruption()
        {
            var directory = Dir("refuse-corrupt");

            using (var journal = new WriteAheadJournal(directory, Session,
                       DurabilityPolicy.SyncEachRecord))
            {
                journal.Append(JournalRecordType.Message, 1, 0, Payload(1));
            }

            var segment = Directory.GetFiles(directory, "segment-*.jrn").Single();
            var bytes = File.ReadAllBytes(segment);
            bytes[bytes.Length - JournalRecord.TrailerSize - 1] ^= 1;
            File.WriteAllBytes(segment, bytes);

            Assert.Throws<InvalidDataException>(() =>
                new WriteAheadJournal(directory, Session, DurabilityPolicy.SyncEachRecord));
        }

        [Fact]
        public void ADeletedMiddleSegmentIsCorruption()
        {
            var directory = Dir("missing-segment");
            var segmentBytes = JournalRecord.SizeFor(16) +
                JournalRecord.SizeFor(JournalRecord.MaxPayloadSize);
            var payload = new byte[JournalRecord.MaxPayloadSize];

            using (var journal = new WriteAheadJournal(directory, Session,
                       DurabilityPolicy.OsBuffered, segmentBytes))
            {
                for (ulong sequence = 1; sequence <= 4; sequence++)
                    journal.Append(JournalRecordType.Message, sequence, 0, payload);
            }

            var segments = Directory.GetFiles(directory, "segment-*.jrn").OrderBy(x => x).ToArray();
            Assert.True(segments.Length >= 4);
            File.Delete(segments[1]);

            var report = JournalReader.Recover(directory);
            Assert.Equal(RecoveryOutcome.Corrupt, report.Outcome);
            Assert.Equal(JournalReadResult.SegmentOrder, report.Failure);
        }

        [Fact]
        public void ADeletedFirstSegmentIsCorruption()
        {
            var directory = Dir("missing-first-segment");
            var segmentBytes = JournalRecord.SizeFor(16) +
                JournalRecord.SizeFor(JournalRecord.MaxPayloadSize);
            var payload = new byte[JournalRecord.MaxPayloadSize];

            using (var journal = new WriteAheadJournal(directory, Session,
                       DurabilityPolicy.OsBuffered, segmentBytes))
            {
                journal.AppendNext(0, payload);
                journal.AppendNext(0, payload);
            }

            var segments = Directory.GetFiles(directory, "segment-*.jrn").OrderBy(x => x).ToArray();
            Assert.True(segments.Length >= 2);
            File.Delete(segments[0]);

            var report = JournalReader.Recover(directory);
            Assert.Equal(RecoveryOutcome.Corrupt, report.Outcome);
            Assert.Equal(JournalReadResult.SegmentOrder, report.Failure);
        }

        [Fact]
        public void ASecondWriterCannotAcquireTheJournal()
        {
            var directory = Dir("writer-lease");
            using var first = new WriteAheadJournal(directory, Session, DurabilityPolicy.OsBuffered);

            Assert.Throws<IOException>(() =>
                new WriteAheadJournal(directory, Session, DurabilityPolicy.OsBuffered));
        }

        [Fact]
        public async Task PeriodicDurabilitySyncsAnIdleWriter()
        {
            var directory = Dir("periodic-idle");
            using var journal = new WriteAheadJournal(directory, Session,
                DurabilityPolicy.SyncPeriodic, syncInterval: TimeSpan.FromMilliseconds(10));
            journal.Sync();
            var before = journal.Syncs;
            journal.Append(JournalRecordType.Message, 1, 0, Payload(1));

            var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
            while (journal.Syncs == before && Stopwatch.GetTimestamp() < deadline)
                await Task.Delay(10, TestContext.Current.CancellationToken);

            Assert.True(journal.Syncs > before, "the idle periodic writer never reached storage");
        }

        [Fact]
        public void OsBufferedAppendAllocatesNothingInSteadyState()
        {
            var directory = Dir("append-allocation");
            var payload = new byte[64];
            using var journal = new WriteAheadJournal(directory, Session,
                DurabilityPolicy.OsBuffered);

            journal.AppendNext(0, payload);
            journal.AppendNext(0, payload);
            var before = GC.GetAllocatedBytesForCurrentThread();

            for (var i = 0; i < 1_000; i++)
                journal.AppendNext(i, payload);

            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(0, allocated);
        }

        [Fact]
        public void FeedPacketsAdvanceByTheirMessageCount()
        {
            var directory = Dir("packet-range");
            var packet = new byte[FeedProtocol.HeaderSize + 2 * FeedProtocol.IncrementalSize];
            var offset = FeedProtocol.HeaderSize;
            offset += FeedProtocol.WriteIncremental(packet.AsSpan(offset), FeedMessageType.Add, 1,
                Side.Bid, new PriceLevel(-1, 100));
            offset += FeedProtocol.WriteIncremental(packet.AsSpan(offset), FeedMessageType.Add, 1,
                Side.Bid, new PriceLevel(-2, 100));
            FeedProtocol.WriteHeader(packet.AsSpan(0, offset), 2, Session, 0, 1);

            using (var journal = new WriteAheadJournal(directory, Session,
                       DurabilityPolicy.SyncEachRecord, initialSequence: 0))
            {
                journal.AppendPacket(packet.AsSpan(0, offset));
                Assert.Equal(2UL, journal.NextSequence);
            }

            var result = JournalReader.TryReadRange(directory, Session, 0, 1, out var found);
            Assert.Equal(JournalRangeResult.Success, result);
            Assert.Single(found);
            Assert.Equal(2, found[0].MessageCount);
            Assert.Equal(packet.AsSpan(0, offset).ToArray(), found[0].Payload);
        }

        [Fact]
        public void GapFillDoesNotReturnAPartialFeedPacket()
        {
            var directory = Dir("packet-alignment");
            var packet = new byte[FeedProtocol.HeaderSize + 2 * FeedProtocol.IncrementalSize];
            var offset = FeedProtocol.HeaderSize;
            offset += FeedProtocol.WriteIncremental(packet.AsSpan(offset), FeedMessageType.Add, 1,
                Side.Bid, new PriceLevel(-1, 100));
            offset += FeedProtocol.WriteIncremental(packet.AsSpan(offset), FeedMessageType.Add, 1,
                Side.Bid, new PriceLevel(-2, 100));
            FeedProtocol.WriteHeader(packet.AsSpan(0, offset), 2, Session, 0, 1);

            using (var journal = new WriteAheadJournal(directory, Session,
                       DurabilityPolicy.SyncEachRecord, initialSequence: 0))
                journal.AppendPacket(packet.AsSpan(0, offset));

            var result = JournalReader.TryReadRange(directory, Session, 1, 1, out var found);

            Assert.Equal(JournalRangeResult.Missing, result);
            Assert.Empty(found);
        }

        [Fact]
        public void PublisherRejectsAnInvalidBatchBound()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new MulticastPublisher(
                IPAddress.Parse("239.7.7.77"), 31777, IPAddress.Loopback, maxBatch: 0));
        }

        [Fact]
        public void PublisherPersistsTheSealedPacket()
        {
            var directory = Dir("publisher-journal");
            using var journal = new WriteAheadJournal(directory, Session,
                DurabilityPolicy.SyncEachRecord, initialSequence: 0);

            using (var publisher = new MulticastPublisher(IPAddress.Parse("239.7.7.77"), 31777,
                       IPAddress.Loopback, maxBatch: 1, sessionId: Session, journal: journal))
            {
                publisher.PublishSnapshot(1, ReadOnlySpan<PriceLevel>.Empty,
                    ReadOnlySpan<PriceLevel>.Empty);
                publisher.Flush();
            }

            var records = new List<byte[]>();
            var report = JournalReader.Recover(directory, (in JournalRecordView record) =>
            {
                if (record.Type == JournalRecordType.FeedPacket)
                    records.Add(record.Payload.ToArray());
                return true;
            });

            Assert.Equal(RecoveryOutcome.Clean, report.Outcome);
            Assert.Single(records);
            Assert.True(FeedProtocol.TryReadHeader(records[0], out var header, out _));
            Assert.Equal(Session, header.SessionId);
            Assert.Equal(0UL, header.FirstSequence);
        }

        [Fact]
        public async Task FeedGapRepairReplaysPacketsBeforeTheHeldLivePacket()
        {
            var directory = Dir("feed-repair");
            var snapshot = SnapshotPacket(0);
            var first = IncrementalPacket(1, -1);
            var second = IncrementalPacket(2, -2);

            using var journal = new WriteAheadJournal(directory, Session,
                DurabilityPolicy.SyncEachRecord, initialSequence: 0);
            journal.AppendPacket(snapshot);
            journal.AppendPacket(first);
            journal.AppendPacket(second);

            using var service = new RetransmissionService(directory);
            service.Start();
            var decoder = new FeedDecoder(_ => new SortedArrayBook(10));
            decoder.Consume(snapshot);
            var recovery = new FeedRecoveryCoordinator(decoder,
                new RetransmissionClient(service.Port));

            var result = await recovery.ConsumeAsync(second, TestContext.Current.CancellationToken);

            Assert.Equal(GapRecoveryResult.Repaired, result);
            Assert.False(decoder.IsStale);
            Assert.Equal(3UL, decoder.ExpectedSequence);
            Assert.Equal(new[] { -1, -2 },
                decoder.BookFor(1).ToList(Side.Bid).Select(level => level.Price));
            Assert.Equal(0, decoder.Statistics.Gaps);
        }

        [Fact]
        public async Task MissingGapFillRequiresASnapshot()
        {
            var directory = Dir("feed-repair-missing");
            var snapshot = SnapshotPacket(0);
            var future = IncrementalPacket(2, -2);

            using var journal = new WriteAheadJournal(directory, Session,
                DurabilityPolicy.SyncEachRecord, initialSequence: 0);
            journal.AppendPacket(snapshot);

            using var service = new RetransmissionService(directory);
            service.Start();
            var decoder = new FeedDecoder(_ => new SortedArrayBook(10));
            decoder.Consume(snapshot);
            var recovery = new FeedRecoveryCoordinator(decoder,
                new RetransmissionClient(service.Port));

            var result = await recovery.ConsumeAsync(future, TestContext.Current.CancellationToken);

            Assert.Equal(GapRecoveryResult.SnapshotRequired, result);
            Assert.True(decoder.IsStale);
            Assert.Equal(1, decoder.Statistics.Gaps);
        }

        [Fact]
        public async Task RetransmissionRefusesOverflowAndWrongSession()
        {
            var directory = Dir("request-validation");
            using (var journal = new WriteAheadJournal(directory, Session,
                       DurabilityPolicy.SyncEachRecord))
                journal.Append(JournalRecordType.Message, 1, 0, Payload(1));

            using var service = new RetransmissionService(directory);
            service.Start();
            var client = new RetransmissionClient(service.Port);

            var overflow = await client.RequestDetailedAsync(Session, 0, ulong.MaxValue,
                TestContext.Current.CancellationToken);
            var wrongSession = await client.RequestDetailedAsync(Session + 1, 1, 1,
                TestContext.Current.CancellationToken);

            Assert.Equal(RetransmissionStatus.InvalidRequest, overflow.Status);
            Assert.Equal(RetransmissionStatus.WrongSession, wrongSession.Status);
            Assert.Equal(2, service.RequestsRefused);
        }

        [Fact]
        public async Task RetransmissionReportsLiveTailCorruption()
        {
            var directory = Dir("request-corrupt-tail");
            using var journal = new WriteAheadJournal(directory, Session,
                DurabilityPolicy.OsBuffered);
            journal.AppendNext(0, Payload(1));

            using var service = new RetransmissionService(directory);
            service.Start();
            journal.AppendNext(0, Payload(2));

            var segment = Directory.GetFiles(directory, "segment-*.jrn").Single();
            using (var stream = new FileStream(segment, FileMode.Open, FileAccess.ReadWrite,
                       FileShare.ReadWrite))
            {
                stream.Position = stream.Length - JournalRecord.TrailerSize - 1;
                var value = stream.ReadByte();
                stream.Position--;
                stream.WriteByte((byte)(value ^ 1));
                stream.Flush();
            }

            var response = await new RetransmissionClient(service.Port).RequestDetailedAsync(
                Session, 1, 2, TestContext.Current.CancellationToken);

            Assert.Equal(RetransmissionStatus.CorruptJournal, response.Status);
            Assert.Empty(response.Messages);
        }

        [Fact]
        public void CheckpointsDetectEverySingleBitCorruption()
        {
            var journalDirectory = Dir("checkpoint-crc-journal");
            var checkpointDirectory = Dir("checkpoint-crc");
            string path;

            using (var journal = new WriteAheadJournal(journalDirectory, Session,
                       DurabilityPolicy.SyncEachRecord))
            {
                journal.Append(JournalRecordType.Message, 1, 0, Payload(1));
                var book = new SortedArrayBook(4);
                book.Upsert(Side.Bid, -1, 100);
                book.Upsert(Side.Ask, 1, 100);
                path = Checkpoint.Write(checkpointDirectory, journal, 1, Session,
                    new Dictionary<int, IOrderBook> { [1] = book });
            }

            var original = File.ReadAllBytes(path);

            for (var index = 0; index < original.Length; index++)
            {
                for (var bit = 0; bit < 8; bit++)
                {
                    var damaged = (byte[])original.Clone();
                    damaged[index] ^= (byte)(1 << bit);
                    File.WriteAllBytes(path, damaged);

                    var target = new Dictionary<int, IOrderBook>
                    {
                        [99] = new SortedArrayBook(1),
                    };

                    Assert.Throws<InvalidDataException>(() =>
                        Checkpoint.Restore(path, _ => new SortedArrayBook(4), target, Session));
                    Assert.True(target.ContainsKey(99), "failed restore mutated the target state");
                }
            }

            File.WriteAllBytes(path, original);
            Assert.Throws<InvalidDataException>(() => Checkpoint.Restore(path,
                _ => new SortedArrayBook(4), new Dictionary<int, IOrderBook>(), Session + 1));
        }

        [Fact]
        public void ASequenceZeroCheckpointCanBeDiscovered()
        {
            var journalDirectory = Dir("checkpoint-zero-journal");
            var checkpointDirectory = Dir("checkpoint-zero");
            var packet = SnapshotPacket(0);
            string written;

            using (var journal = new WriteAheadJournal(journalDirectory, Session,
                       DurabilityPolicy.SyncEachRecord, initialSequence: 0))
            {
                journal.AppendPacket(packet);
                written = Checkpoint.Write(checkpointDirectory, journal, 0, Session,
                    new Dictionary<int, IOrderBook> { [1] = new SortedArrayBook(4) });
            }

            Assert.Equal(written, Checkpoint.FindLatest(checkpointDirectory));
        }

        [Fact]
        public void SequencerExhaustionNeverWraps()
        {
            var sequencer = new Sequencer(ulong.MaxValue - 1);
            Assert.Equal(ulong.MaxValue, sequencer.Next());
            Assert.Throws<OverflowException>(() => sequencer.Next());
            Assert.Equal(ulong.MaxValue, sequencer.Last);
        }

        [Fact]
        public void SparseRangeIndexRefreshesTheLiveTail()
        {
            var directory = Dir("sparse-index");
            using var journal = new WriteAheadJournal(directory, Session, DurabilityPolicy.OsBuffered);

            for (ulong sequence = 1; sequence <= 1_000; sequence++)
                journal.Append(JournalRecordType.Message, sequence, 0, Payload((int)sequence));

            var reader = new JournalRangeReader(directory, stride: 32);
            Assert.True(reader.IndexEntries >= 31);

            for (ulong sequence = 1_001; sequence <= 2_000; sequence++)
                journal.Append(JournalRecordType.Message, sequence, 0, Payload((int)sequence));

            var result = reader.TryRead(Session, 1_990, 2_000, out var found);

            Assert.Equal(JournalRangeResult.Success, result);
            Assert.Equal(11, found.Count);
            Assert.Equal(Enumerable.Range(1990, 11).Select(value => (ulong)value),
                found.Select(message => message.Sequence));
            Assert.True(reader.IndexEntries >= 62);
        }
    }
}
