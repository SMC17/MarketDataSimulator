using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;
using MarketData.Common.Books;
using MarketData.Common.Durability;
using MarketData.Common.Feed;

namespace MarketData.Bench
{
    /// <summary>WAL acknowledgement, replay, checkpoint, and range-read costs.</summary>
    public static class DurabilityBenchmark
    {
        private const ulong Session = 0xD0A8_1EUL;

        public static int Run(string[] args)
        {
            var records = 10_000;
            var payloadBytes = 64;
            var trials = 5;
            var rangeQueries = 200;
            string outputPath = null;

            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--records": records = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--payload": payloadBytes = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--trials": trials = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--range-queries": rangeQueries = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--out": outputPath = args[++i]; break;
                    default: throw new ArgumentException($"Unknown durability option: {args[i]}");
                }
            }

            if (records is < 100 or > 10_000_000 || payloadBytes < 0 ||
                payloadBytes > JournalRecord.MaxPayloadSize || trials is < 3 or > 101 ||
                rangeQueries is < 10 or > 1_000_000)
                throw new ArgumentOutOfRangeException(nameof(args));

            var root = Path.Combine(Path.GetTempPath(), "mds-durability-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);

            try
            {
                var append = MeasureAppendCases(root, records, payloadBytes, trials);
                var recovery = MeasureRecovery(root, Math.Max(records, 50_000), trials);
                var ranges = MeasureRanges(root, Math.Max(records, 50_000), rangeQueries, trials);

                Print(append, recovery, ranges);

                var report = new DurabilityReport(
                    DateTimeOffset.UtcNow,
                    RuntimeInformation.FrameworkDescription,
                    RuntimeInformation.OSDescription,
                    RuntimeInformation.ProcessArchitecture.ToString(),
                    Environment.ProcessorCount,
                    GCSettings.IsServerGC,
                    Stopwatch.Frequency,
                    Crc32C.Implementation,
                    records,
                    payloadBytes,
                    trials,
                    append,
                    recovery,
                    ranges);

                if (outputPath is not null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
                    File.WriteAllText(outputPath, JsonSerializer.Serialize(report,
                        new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
                    Console.WriteLine($"Wrote {outputPath}");
                }

                return 0;
            }
            finally
            {
                try { Directory.Delete(root, recursive: true); } catch (IOException) { }
            }
        }

        private static AppendResult[] MeasureAppendCases(string root, int records, int payloadBytes,
            int trials)
        {
            var cases = new[]
            {
                new AppendCase("OS page cache", DurabilityPolicy.OsBuffered, TimeSpan.FromMilliseconds(200), 0),
                new AppendCase("periodic 1 ms", DurabilityPolicy.SyncPeriodic, TimeSpan.FromMilliseconds(1), 0),
                new AppendCase("group commit 64", DurabilityPolicy.OsBuffered, TimeSpan.FromMilliseconds(200), 64),
                new AppendCase("fsync each", DurabilityPolicy.SyncEachRecord, TimeSpan.FromMilliseconds(200), 0),
            };

            var results = new List<AppendResult>(cases.Length + 1);
            foreach (var item in cases)
                results.Add(MeasureAppend(root, item, records, payloadBytes, trials));
            results.Add(MeasureFeedPacketAppend(root, records, trials));
            return results.ToArray();
        }

        private static AppendResult MeasureAppend(string root, AppendCase item, int records,
            int payloadBytes, int trials)
        {
            var elapsed = new double[trials];
            var allocated = new double[trials];
            var syncs = new long[trials];
            var payload = new byte[payloadBytes];

            for (var trial = -1; trial < trials; trial++)
            {
                var directory = Path.Combine(root, $"append-{item.Name.Replace(' ', '-')}-{trial}");
                using (var journal = new WriteAheadJournal(directory, Session, item.Policy,
                           syncInterval: item.Interval))
                {
                    var before = GC.GetAllocatedBytesForCurrentThread();
                    var started = Stopwatch.GetTimestamp();

                    for (var sequence = 1; sequence <= records; sequence++)
                    {
                        journal.Append(JournalRecordType.Message, (ulong)sequence, sequence, payload);
                        if (item.GroupSize != 0 && sequence % item.GroupSize == 0)
                            journal.Sync();
                    }

                    if (item.GroupSize != 0 && records % item.GroupSize != 0)
                        journal.Sync();

                    var ticks = Stopwatch.GetTimestamp() - started;
                    var bytes = GC.GetAllocatedBytesForCurrentThread() - before;

                    if (trial >= 0)
                    {
                        elapsed[trial] = ticks * (1_000_000_000.0 / Stopwatch.Frequency) / records;
                        allocated[trial] = bytes / (double)records;
                        syncs[trial] = journal.Syncs;
                    }
                }

                Directory.Delete(directory, recursive: true);
            }

            return SummariseAppend(item.Name, payloadBytes, records, elapsed, allocated, syncs,
                item.Policy, item.Interval.TotalMilliseconds);
        }

        private static AppendResult MeasureFeedPacketAppend(string root, int records, int trials)
        {
            var elapsed = new double[trials];
            var allocated = new double[trials];
            var syncs = new long[trials];
            var packet = new byte[FeedProtocol.HeaderSize + FeedProtocol.IncrementalSize];
            FeedProtocol.WriteIncremental(packet.AsSpan(FeedProtocol.HeaderSize), FeedMessageType.Add,
                1, Side.Bid, new PriceLevel(-1, 100));

            for (var trial = -1; trial < trials; trial++)
            {
                var directory = Path.Combine(root, $"feed-packet-{trial}");
                using (var journal = new WriteAheadJournal(directory, Session,
                           DurabilityPolicy.OsBuffered, initialSequence: 0))
                {
                    var before = GC.GetAllocatedBytesForCurrentThread();
                    var started = Stopwatch.GetTimestamp();

                    for (var sequence = 0; sequence < records; sequence++)
                    {
                        FeedProtocol.WriteHeader(packet, 1, Session, (ulong)sequence, sequence);
                        journal.AppendPacket(packet);
                    }

                    var ticks = Stopwatch.GetTimestamp() - started;
                    var bytes = GC.GetAllocatedBytesForCurrentThread() - before;

                    if (trial >= 0)
                    {
                        elapsed[trial] = ticks * (1_000_000_000.0 / Stopwatch.Frequency) / records;
                        allocated[trial] = bytes / (double)records;
                        syncs[trial] = journal.Syncs;
                    }
                }

                Directory.Delete(directory, recursive: true);
            }

            return SummariseAppend("seal + packet WAL", packet.Length, records, elapsed, allocated,
                syncs, DurabilityPolicy.OsBuffered, 0);
        }

        private static RecoveryResult MeasureRecovery(string root, int messages, int trials)
        {
            var directory = Path.Combine(root, "recovery-journal");
            var checkpoints = Path.Combine(root, "recovery-checkpoints");
            var random = new Random(20260819);
            var books = new Dictionary<int, IOrderBook>();
            var encoded = new byte[13];
            var checkpointSequence = messages - messages / 20;
            var segmentBytes = JournalRecord.SizeFor(16) +
                JournalRecord.SizeFor(JournalRecord.MaxPayloadSize);

            using (var journal = new WriteAheadJournal(directory, Session,
                       DurabilityPolicy.OsBuffered, segmentBytes))
            {
                for (var sequence = 1; sequence <= messages; sequence++)
                {
                    var instrument = random.Next(1, 4);
                    var side = random.Next(2) == 0 ? Side.Bid : Side.Ask;
                    var price = random.Next(-50, 51);
                    var quantity = (uint)random.Next(0, 500);

                    if (!books.TryGetValue(instrument, out var book))
                        books[instrument] = book = new SortedArrayBook(16);
                    book.Upsert(side, price, quantity);

                    BinaryPrimitives.WriteInt32LittleEndian(encoded, instrument);
                    encoded[4] = (byte)side;
                    BinaryPrimitives.WriteInt32LittleEndian(encoded.AsSpan(5), price);
                    BinaryPrimitives.WriteUInt32LittleEndian(encoded.AsSpan(9), quantity);
                    journal.Append(JournalRecordType.Message, (ulong)sequence, sequence, encoded);

                    if (sequence == checkpointSequence)
                        Checkpoint.Write(checkpoints, journal, (ulong)sequence, Session, books);
                }
            }

            var checkpoint = Checkpoint.FindLatest(checkpoints)!;
            _ = TimeRecovery(directory, null);
            _ = TimeRecovery(directory, checkpoint);
            var full = new double[trials];
            var incremental = new double[trials];

            for (var trial = 0; trial < trials; trial++)
            {
                if ((trial & 1) == 0)
                {
                    full[trial] = TimeRecovery(directory, null);
                    incremental[trial] = TimeRecovery(directory, checkpoint);
                }
                else
                {
                    incremental[trial] = TimeRecovery(directory, checkpoint);
                    full[trial] = TimeRecovery(directory, null);
                }
            }

            Array.Sort(full);
            Array.Sort(incremental);
            return new RecoveryResult(messages, checkpointSequence,
                Round(full[trials / 2]), Round(full[0]), Round(full[^1]),
                Round(incremental[trials / 2]), Round(incremental[0]), Round(incremental[^1]),
                Math.Round(full[trials / 2] / incremental[trials / 2], 2));
        }

        private static RangeResult[] MeasureRanges(string root, int messages, int queries, int trials)
        {
            var directory = Path.Combine(root, "range-journal");
            var segmentBytes = JournalRecord.SizeFor(16) +
                JournalRecord.SizeFor(JournalRecord.MaxPayloadSize);
            var payload = new byte[64];

            using (var journal = new WriteAheadJournal(directory, Session,
                       DurabilityPolicy.OsBuffered, segmentBytes))
            {
                for (var sequence = 1; sequence <= messages; sequence++)
                    journal.Append(JournalRecordType.Message, (ulong)sequence, sequence, payload);
            }

            var index = new JournalRangeReader(directory);
            var starts = new ulong[queries];
            var random = new Random(17);
            for (var i = 0; i < starts.Length; i++)
                starts[i] = (ulong)random.Next(1, messages - 10);

            return new[]
            {
                MeasureRanges("sparse index", queries, trials, starts,
                    (ulong from, ulong to, out List<SequencedPayload> found) =>
                        index.TryRead(Session, from, to, out found), index.IndexEntries),
                MeasureRanges("segment scan", queries, trials, starts,
                    (ulong from, ulong to, out List<SequencedPayload> found) =>
                        JournalReader.TryReadRange(directory, Session, from, to, out found), 0),
            };
        }

        private static RangeResult MeasureRanges(string name, int queries, int trials,
            ulong[] starts, RangeRead read, int indexEntries)
        {
            _ = RunRangeQueries(starts, read);
            var elapsed = new double[trials];
            var allocated = new double[trials];

            for (var trial = 0; trial < trials; trial++)
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                var started = Stopwatch.GetTimestamp();
                var sink = RunRangeQueries(starts, read);
                var ticks = Stopwatch.GetTimestamp() - started;
                elapsed[trial] = ticks * (1_000_000_000.0 / Stopwatch.Frequency) / queries;
                allocated[trial] = (GC.GetAllocatedBytesForCurrentThread() - before) / (double)queries;
                GC.KeepAlive(sink);
            }

            Array.Sort(elapsed);
            Array.Sort(allocated);
            return new RangeResult(name, queries, indexEntries, Round(elapsed[trials / 2]),
                Round(elapsed[0]), Round(elapsed[^1]), Round(allocated[trials / 2]));
        }

        private static ulong RunRangeQueries(ulong[] starts, RangeRead read)
        {
            ulong sink = 0;

            foreach (var from in starts)
            {
                if (read(from, from + 9, out var found) != JournalRangeResult.Success ||
                    found.Count != 10)
                    throw new InvalidOperationException("Prepared range was not recoverable.");
                sink ^= found[^1].Sequence;
            }

            return sink;
        }

        private static double TimeRecovery(string directory, string checkpointPath)
        {
            var books = new Dictionary<int, IOrderBook>();
            ulong from = Sequencer.None;
            var started = Stopwatch.GetTimestamp();

            if (checkpointPath is not null)
                from = Checkpoint.Restore(checkpointPath, _ => new SortedArrayBook(16), books, Session);

            var report = JournalReader.Recover(directory, (in JournalRecordView record) =>
            {
                if (record.Type != JournalRecordType.Message || record.Sequence <= from)
                    return true;

                var payload = record.Payload;
                var instrument = BinaryPrimitives.ReadInt32LittleEndian(payload);
                var side = (Side)payload[4];
                var price = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(5));
                var quantity = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(9));

                if (!books.TryGetValue(instrument, out var book))
                    books[instrument] = book = new SortedArrayBook(16);
                book.Upsert(side, price, quantity);
                return true;
            }, from, Session);

            if (report.Outcome != RecoveryOutcome.Clean)
                throw new InvalidDataException("Prepared recovery journal failed validation.");

            GC.KeepAlive(books);
            return (Stopwatch.GetTimestamp() - started) * (1_000.0 / Stopwatch.Frequency);
        }

        private static AppendResult SummariseAppend(string name, int payloadBytes, int records,
            double[] elapsed, double[] allocated, long[] syncs, DurabilityPolicy policy,
            double intervalMilliseconds)
        {
            Array.Sort(elapsed);
            Array.Sort(allocated);
            Array.Sort(syncs);
            var median = elapsed[elapsed.Length / 2];
            var rate = 1_000_000_000.0 / median;

            return new AppendResult(name, policy.ToString(), payloadBytes, records,
                intervalMilliseconds, syncs[syncs.Length / 2], Round(median), Round(elapsed[0]),
                Round(elapsed[^1]), Math.Round(rate, 0),
                Math.Round(rate * JournalRecord.SizeFor(payloadBytes) / 1024 / 1024, 2),
                Round(allocated[allocated.Length / 2]));
        }

        private static void Print(AppendResult[] append, RecoveryResult recovery,
            RangeResult[] ranges)
        {
            Console.WriteLine("Durability acknowledgement path");
            Console.WriteLine($"{"Case",20} {"median ns",12} {"min-max ns",20} {"ops/s",12} {"syncs",8} {"B/op",8}");
            foreach (var item in append)
                Console.WriteLine($"{item.Name,20} {item.MedianNanoseconds,12:N1} " +
                    $"{item.MinNanoseconds:N1}-{item.MaxNanoseconds,10:N1} " +
                    $"{item.AppendsPerSecond,12:N0} {item.MedianSyncs,8:N0} " +
                    $"{item.BytesAllocatedPerAppend,8:N3}");

            Console.WriteLine();
            Console.WriteLine($"Recovery {recovery.Messages:N0}: full {recovery.FullMedianMilliseconds:N2} ms; " +
                $"checkpoint@{recovery.CheckpointSequence:N0} {recovery.CheckpointMedianMilliseconds:N2} ms; " +
                $"{recovery.SpeedUp:N2}x");
            foreach (var item in ranges)
                Console.WriteLine($"Range {item.Name}: {item.MedianNanosecondsPerRequest:N0} ns/request, " +
                    $"{item.BytesAllocatedPerRequest:N0} B/request, index entries {item.IndexEntries:N0}");
        }

        private static double Round(double value) => Math.Round(value, 2);

        private delegate JournalRangeResult RangeRead(ulong from, ulong to,
            out List<SequencedPayload> found);

        private sealed record AppendCase(string Name, DurabilityPolicy Policy, TimeSpan Interval,
            int GroupSize);

        private sealed record AppendResult(string Name, string Policy, int PayloadBytes, int Records,
            double SyncIntervalMilliseconds, long MedianSyncs, double MedianNanoseconds,
            double MinNanoseconds, double MaxNanoseconds, double AppendsPerSecond,
            double MegabytesPerSecond, double BytesAllocatedPerAppend);

        private sealed record RecoveryResult(int Messages, int CheckpointSequence,
            double FullMedianMilliseconds, double FullMinMilliseconds, double FullMaxMilliseconds,
            double CheckpointMedianMilliseconds, double CheckpointMinMilliseconds,
            double CheckpointMaxMilliseconds, double SpeedUp);

        private sealed record RangeResult(string Name, int Queries, int IndexEntries,
            double MedianNanosecondsPerRequest, double MinNanosecondsPerRequest,
            double MaxNanosecondsPerRequest, double BytesAllocatedPerRequest);

        private sealed record DurabilityReport(DateTimeOffset TimestampUtc, string Runtime,
            string OperatingSystem, string Architecture, int LogicalProcessors, bool ServerGc,
            long StopwatchFrequency, string Crc32CImplementation, int Records, int PayloadBytes,
            int Trials, AppendResult[] Append, RecoveryResult Recovery, RangeResult[] RangeReads);
    }
}
