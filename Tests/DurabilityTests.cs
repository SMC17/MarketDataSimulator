using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MarketData.Common.Books;
using MarketData.Common.Durability;
using Xunit;

namespace MarketData.Tests
{
    /// <summary>
    /// The durability layer, tested the only way that means anything: by damaging it.
    /// </summary>
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

        /// <summary>Every single-byte truncation must be reported as incomplete, never as valid.</summary>
        /// <remarks>
        /// This is the case a crash actually produces, and the one a naive reader gets wrong: it
        /// finds a plausible header, trusts the length, and reads past the end of what was written.
        /// </remarks>
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
            var segmentBytes = JournalRecord.OverheadSize + JournalRecord.MaxPayloadSize;

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

        /// <summary>
        /// Checkpoint + subsequent journal must equal full replay, exactly.
        /// </summary>
        /// <remarks>
        /// The invariant the whole recovery story rests on. Asserted against a book rebuilt from
        /// scratch rather than against itself, because a checkpoint that is confidently wrong is
        /// worse than no checkpoint at all.
        /// </remarks>
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
                for (var i = 0; i < operations.Count; i++)
                {
                    var op = operations[i];
                    var sequence = (ulong)(i + 1);

                    if (!live.TryGetValue(op.Instrument, out var book))
                        live[op.Instrument] = book = Make(op.Instrument);

                    book.Upsert(op.Side, op.Price, op.Quantity);

                    Span<byte> encoded = stackalloc byte[13];
                    System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(encoded, op.Instrument);
                    encoded[4] = (byte)op.Side;
                    System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(encoded.Slice(5), op.Price);
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(encoded.Slice(9), op.Quantity);

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
                    Checkpoint.Write(checkpoints, journal, sequence, Session, books);
            }

            Assert.Equal(3, Checkpoint.Prune(checkpoints, keep: 3));

            var remaining = Directory.GetFiles(checkpoints, "checkpoint-*.chk")
                .Select(Checkpoint.SequenceOf).OrderBy(s => s).ToArray();

            Assert.Equal(new ulong[] { 4, 5, 6 }, remaining);
            Assert.Throws<ArgumentOutOfRangeException>(() => Checkpoint.Prune(checkpoints, keep: 0));
        }

        /// <summary>
        /// Recovering from a checkpoint must not re-read the history behind it.
        /// </summary>
        /// <remarks>
        /// The point of a checkpoint is that recovery stops being proportional to uptime. An
        /// implementation that restores the checkpoint and *then* scans the whole log anyway is
        /// still O(uptime) and has bought nothing but the book replay - which is what the first
        /// version here did, and what the recovery benchmark exposed. This asserts the skip
        /// happens, by counting the records the scan actually visits.
        /// </remarks>
        [Fact]
        public void RecoveringFromACheckpointSkipsSegmentsBelowIt()
        {
            var directory = Dir("skip-journal");
            var payload = new byte[512];
            var segmentBytes = JournalRecord.OverheadSize + JournalRecord.MaxPayloadSize;
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
            var segmentBytes = JournalRecord.OverheadSize + JournalRecord.MaxPayloadSize;
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
            var recovered = await client.RequestAsync(100, 109);

            Assert.NotNull(recovered);
            Assert.Equal(10, recovered.Count);
            Assert.Equal(Enumerable.Range(100, 10).Select(i => (ulong)i), recovered.Select(m => m.Sequence));

            for (var i = 0; i < recovered.Count; i++)
                Assert.Equal(Payload(100 + i), recovered[i].Payload);
        }

        /// <summary>
        /// A gap too large to fill from history is refused, not served slowly.
        /// </summary>
        /// <remarks>
        /// Retransmission is where one struggling subscriber can become everybody's problem. A
        /// subscriber this far behind should take a snapshot, which costs O(book) rather than
        /// O(history).
        /// </remarks>
        [Fact]
        public async Task AnOversizedRequestIsRefusedRatherThanServed()
        {
            var directory = Dir("gapfill-refuse");

            using (var journal = new WriteAheadJournal(directory, Session, DurabilityPolicy.OsBuffered))
                journal.Append(JournalRecordType.Message, 1, 0, Payload(1));

            using var service = new RetransmissionService(directory);
            service.Start();

            var client = new RetransmissionClient(service.Port);
            var refused = await client.RequestAsync(1, RetransmissionService.MaxRangeLength + 1);

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
            });

            var client = new RetransmissionClient(service.Port);
            var recovered = await client.RequestAsync(10, 19);

            await publishing;

            Assert.NotNull(recovered);
            Assert.Equal(10, recovered.Count);
            Assert.Equal(Payload(10), recovered[0].Payload);
        }
    }
}
