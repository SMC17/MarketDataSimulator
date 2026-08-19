using MarketData.Common.Books;
using MarketData.Common.Durability;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MarketData.Bench
{
    /// <summary>
    /// What durability costs, and what recovery costs.
    /// </summary>
    /// <remarks>
    /// Both numbers exist to make a design decision falsifiable rather than asserted. "Journal
    /// every message before publishing it" is only defensible if the append is cheap next to the
    /// publish; "checkpoint periodically" is only worth the machinery if recovery time actually
    /// stops growing with uptime. Neither is obvious, so both are measured.
    /// </remarks>
    public static class DurabilityBenchmark
    {
        public static int Run(string[] args)
        {
            var records = 50_000;
            var payloadBytes = 64;
            var trials = 5;
            string outputPath = null;

            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--records": records = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--payload": payloadBytes = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--trials": trials = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--out": outputPath = args[++i]; break;
                }
            }

            Console.WriteLine($"Durability benchmark: {records:N0} records of {payloadBytes} B, " +
                              $"median of {trials} trials");
            Console.WriteLine();

            var root = Path.Combine(Path.GetTempPath(), "mds-bench-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);

            var results = new List<object>();

            try
            {
                Console.WriteLine($"{"Durability policy",22} {"ns/append",12} {"appends/s",14} {"syncs",10} {"MB/s",9}");
                Console.WriteLine(new string('-', 72));

                foreach (var policy in new[]
                         {
                             DurabilityPolicy.OsBuffered,
                             DurabilityPolicy.SyncPeriodic,
                             DurabilityPolicy.SyncEachRecord,
                         })
                {
                    var samples = new List<double>();
                    long syncs = 0;

                    for (var trial = 0; trial < trials + 1; trial++)
                    {
                        var directory = Path.Combine(root, $"{policy}-{trial}");
                        var payload = new byte[payloadBytes];
                        var stopwatch = Stopwatch.StartNew();

                        using (var journal = new WriteAheadJournal(directory, 1, policy))
                        {
                            for (var i = 1; i <= records; i++)
                                journal.Append(JournalRecordType.Message, (ulong)i, i, payload);

                            stopwatch.Stop();
                            syncs = journal.Syncs;
                        }

                        // First trial is warm-up: it pays for JIT and for creating the directory.
                        if (trial > 0)
                            samples.Add(stopwatch.Elapsed.TotalNanoseconds / records);

                        Directory.Delete(directory, recursive: true);
                    }

                    var median = Median(samples);
                    var perSecond = 1_000_000_000.0 / median;
                    var bytesPerSecond = perSecond * JournalRecord.SizeFor(payloadBytes);

                    var elapsedMs = median * records / 1_000_000.0;

                    // A periodic policy that never reached its interval synced zero times, and
                    // reporting that without saying why invites the reader to conclude it never
                    // syncs at all. State the run length against the interval instead.
                    var note = policy == DurabilityPolicy.SyncPeriodic && syncs == 0
                        ? $"  (run took {elapsedMs:N0} ms, under the {WriteAheadJournal.FlushInterval.TotalMilliseconds:N0} ms interval, so no periodic sync fell due)"
                        : string.Empty;

                    Console.WriteLine($"{policy,22} {median,12:N1} {perSecond,14:N0} {syncs,10:N0} " +
                                      $"{bytesPerSecond / 1024 / 1024,9:N1}{note}");

                    results.Add(new
                    {
                        Kind = "append",
                        Policy = policy.ToString(),
                        PayloadBytes = payloadBytes,
                        NanosecondsPerAppend = Math.Round(median, 1),
                        AppendsPerSecond = Math.Round(perSecond, 0),
                        Syncs = syncs,
                        RunMilliseconds = Math.Round(median * records / 1_000_000.0, 1),
                        FlushIntervalMilliseconds = WriteAheadJournal.FlushInterval.TotalMilliseconds,
                        MegabytesPerSecond = Math.Round(bytesPerSecond / 1024 / 1024, 1),
                    });
                }

                Console.WriteLine();
                Console.WriteLine("Recovery: rebuilding book state from a journal, with and without a checkpoint.");
                Console.WriteLine();
                Console.WriteLine($"{"Journalled messages",20} {"full replay ms",16} {"from checkpoint ms",20} {"speed-up",10}");
                Console.WriteLine(new string('-', 72));

                foreach (var count in new[] { 10_000, 50_000, 200_000 })
                {
                    var directory = Path.Combine(root, $"recover-{count}");
                    var checkpoints = Path.Combine(root, $"recover-{count}-chk");
                    var random = new Random(count);

                    IOrderBook Make(int _) => new SortedArrayBook(16);
                    var books = new Dictionary<int, IOrderBook>();

                    // Small segments so the log actually rotates, which is what production does and
                    // what lets recovery skip history below the checkpoint. With one giant segment
                    // there is nothing to skip and a checkpoint saves only the book replay, not the
                    // scan - which is exactly the trap the first version of this benchmark fell into.
                    const long segmentBytes = JournalRecord.OverheadSize + JournalRecord.MaxPayloadSize;

                    using (var journal = new WriteAheadJournal(directory, 1, DurabilityPolicy.OsBuffered,
                               segmentBytes))
                    {
                        for (var i = 1; i <= count; i++)
                        {
                            var instrument = random.Next(1, 4);
                            var side = random.Next(2) == 0 ? Side.Bid : Side.Ask;
                            var price = random.Next(-50, 51);
                            var quantity = (uint)random.Next(0, 500);

                            if (!books.TryGetValue(instrument, out var book))
                                books[instrument] = book = Make(instrument);

                            book.Upsert(side, price, quantity);

                            var encoded = new byte[13];
                            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(encoded, instrument);
                            encoded[4] = (byte)side;
                            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(encoded.AsSpan(5), price);
                            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(encoded.AsSpan(9), quantity);

                            journal.Append(JournalRecordType.Message, (ulong)i, i, encoded);

                            // One checkpoint near the end, which is the realistic case: recovery
                            // replays only what has happened since the last one.
                            if (i == count - count / 20)
                                Checkpoint.Write(checkpoints, journal, (ulong)i, 1, books);
                        }
                    }

                    var full = TimeRecovery(directory, null, Make);
                    var incremental = TimeRecovery(directory, Checkpoint.FindLatest(checkpoints), Make);

                    Console.WriteLine($"{count,20:N0} {full,16:N1} {incremental,20:N1} " +
                                      $"{full / Math.Max(incremental, 0.0001),9:N1}x");

                    results.Add(new
                    {
                        Kind = "recovery",
                        Messages = count,
                        FullReplayMilliseconds = Math.Round(full, 1),
                        FromCheckpointMilliseconds = Math.Round(incremental, 1),
                        SpeedUp = Math.Round(full / Math.Max(incremental, 0.0001), 1),
                    });

                    Directory.Delete(directory, recursive: true);
                    if (Directory.Exists(checkpoints))
                        Directory.Delete(checkpoints, recursive: true);
                }

                Console.WriteLine();
                Console.WriteLine("Recovery from a checkpoint is bounded by messages since the checkpoint,");
                Console.WriteLine("not by total uptime. That is the entire reason checkpoints exist.");

                if (outputPath is not null)
                {
                    var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
                    Directory.CreateDirectory(directory);
                    File.WriteAllText(outputPath,
                        JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
                    Console.WriteLine($"Wrote {outputPath}");
                }
            }
            finally
            {
                try { Directory.Delete(root, recursive: true); } catch (IOException) { }
            }

            return 0;
        }

        private static double TimeRecovery(string directory, string checkpointPath, Func<int, IOrderBook> make)
        {
            var books = new Dictionary<int, IOrderBook>();
            var stopwatch = Stopwatch.StartNew();

            var from = Sequencer.None;

            if (checkpointPath is not null)
                from = Checkpoint.Restore(checkpointPath, make, books);

            JournalReader.Recover(directory, (in JournalRecordView record) =>
            {
                if (record.Type != JournalRecordType.Message || record.Sequence <= from)
                    return true;

                var payload = record.Payload;
                var instrument = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(payload);
                var side = (Side)payload[4];
                var price = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(5));
                var quantity = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(9));

                if (!books.TryGetValue(instrument, out var book))
                    books[instrument] = book = make(instrument);

                book.Upsert(side, price, quantity);
                return true;
            }, from);

            stopwatch.Stop();
            return stopwatch.Elapsed.TotalMilliseconds;
        }

        private static double Median(List<double> values)
        {
            var sorted = values.OrderBy(v => v).ToList();
            return sorted.Count == 0 ? 0
                : sorted.Count % 2 == 1 ? sorted[sorted.Count / 2]
                : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
        }
    }
}
