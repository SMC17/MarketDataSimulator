using MarketData.Common.Books;
using MarketData.Common.Lobster;
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
    /// Replays a real NASDAQ session and checks the reconstructed book against the exchange's own.
    /// </summary>
    public static class ReplayBenchmark
    {
        public static int Run(string[] args)
        {
            var directory = "data/lobster";
            var warmup = 0;
            var trials = 3;
            string outputPath = null;

            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--data": directory = args[++i]; break;
                    case "--warmup": warmup = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--trials": trials = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--out": outputPath = args[++i]; break;
                }
            }

            var messagePath = Directory.GetFiles(directory, "*message*.csv").FirstOrDefault();
            var referencePath = Directory.GetFiles(directory, "*orderbook*.csv").FirstOrDefault();

            if (messagePath is null || referencePath is null)
            {
                Console.Error.WriteLine($"No LOBSTER message/orderbook pair found in '{directory}'.");
                Console.Error.WriteLine("Run scripts/fetch-lobster.sh first.");
                return 2;
            }

            Console.WriteLine($"Messages : {Path.GetFileName(messagePath)} ({new FileInfo(messagePath).Length / 1024.0 / 1024.0:F1} MiB)");
            Console.WriteLine($"Reference: {Path.GetFileName(referencePath)} ({new FileInfo(referencePath).Length / 1024.0 / 1024.0:F1} MiB)");

            var messages = File.ReadAllBytes(messagePath);
            var reference = File.ReadAllBytes(referencePath);

            var (minPrice, maxPrice, count) = Survey(messages);
            var (referenceMin, referenceMax) = SurveyReference(reference);
            minPrice = Math.Min(minPrice, referenceMin);
            maxPrice = Math.Max(maxPrice, referenceMax);
            Console.WriteLine($"Messages : {count:N0}   price band [{minPrice:N0}, {maxPrice:N0}] " +
                              $"({(maxPrice - minPrice) / 10000.0:F2} dollars)");
            Console.WriteLine($"Warm-up  : {warmup:N0} messages before comparison begins");
            Console.WriteLine();

            // Padded so a level can never fall outside the ladder's band.
            var band = 10_000;
            var results = new List<ReplayReport>();
            var bestByImplementation = new List<ReplayResult>();

            Console.WriteLine("SINGLE-STEP TRANSITIONS - seed from the exchange's published book, apply one");
            Console.WriteLine("message, compare against the exchange's next published book.");
            Console.WriteLine();
            Console.WriteLine($"{"Implementation",18} {"Verified",14} {"Exact",14} {"Accuracy",11} {"Msgs/s",12} {"Unverifiable",14}");
            Console.WriteLine(new string('-', 92));

            var transitionReports = new List<ReplayReport>();

            foreach (var name in new[] { "SortedArray", "Vectorized", "Ladder", "Tree" })
            {
                ReplayResult best = null;

                for (var trial = 0; trial < trials; trial++)
                {
                    var book = name == "Ladder"
                        ? new LadderBook(4096, minPrice - 10_000, maxPrice + 10_000)
                        : BookFactory.Create(name, 4096, maxPrice + 10_000);

                    var result = LobsterReplay.ReplayTransitions(messages, reference, book);

                    if (best is null || result.MessagesPerSecond > best.MessagesPerSecond)
                        best = result;
                }

                Console.WriteLine($"{name,18} {best.RowsCompared,14:N0} {best.RowsMatched,14:N0} " +
                                  $"{best.MatchRate,10:P4} {best.MessagesPerSecond,12:N0} {best.Unverifiable,14:N0}");

                transitionReports.Add(new ReplayReport(name, best.MessagesApplied, best.RowsCompared,
                    best.RowsMatched, Math.Round(best.MatchRate, 6), Math.Round(best.MessagesPerSecond, 0),
                    best.FirstMismatchRow, best.FirstMismatchDetail, best.NegativeLevels, best.HiddenExecutions));
            }

            var transitionMismatch = transitionReports[0].FirstMismatchDetail;

            if (transitionMismatch is not null)
                Console.WriteLine($"First transition mismatch: {transitionMismatch}");

            Console.WriteLine();
            Console.WriteLine("CUMULATIVE REPLAY - seed once, then apply every message without correction.");
            Console.WriteLine();
            Console.WriteLine($"{"Implementation",18} {"Rows compared",14} {"Matched",14} {"Match rate",11} {"Msgs/s",12} {"First mismatch",15}");
            Console.WriteLine(new string('-', 92));

            foreach (var name in new[] { "SortedArray", "Vectorized", "Ladder", "Tree" })
            {
                ReplayResult best = null;

                for (var trial = 0; trial < trials; trial++)
                {
                    // Depth well beyond the ten levels compared, so the book is a full book and the
                    // comparison is not quietly helped by truncation.
                    var book = name == "Ladder"
                        ? new LadderBook(4096, minPrice - band, maxPrice + band)
                        : BookFactory.Create(name, 4096, maxPrice + band);

                    var result = LobsterReplay.Replay(messages, reference, book, warmup);

                    if (best is null || result.MessagesPerSecond > best.MessagesPerSecond)
                        best = result;
                }

                var mismatch = best.FirstMismatchRow < 0 ? "none" : best.FirstMismatchRow.ToString("N0");
                Console.WriteLine($"{name,18} {best.RowsCompared,14:N0} {best.RowsMatched,14:N0} " +
                                  $"{best.MatchRate,10:P4} {best.MessagesPerSecond,12:N0} {mismatch,15}");

                bestByImplementation.Add(best);
                results.Add(new ReplayReport(name, best.MessagesApplied, best.RowsCompared, best.RowsMatched,
                    Math.Round(best.MatchRate, 6), Math.Round(best.MessagesPerSecond, 0),
                    best.FirstMismatchRow, best.FirstMismatchDetail, best.NegativeLevels, best.HiddenExecutions));
            }

            Console.WriteLine();
            Console.WriteLine("Reconstruction accuracy by depth (share of rows whose top-k levels match exactly):");
            Console.WriteLine();
            Console.WriteLine($"{"Top-k levels",13} {"Rows exact",14} {"Share",10}");
            Console.WriteLine(new string('-', 40));

            var depthCurve = new List<DepthPoint>();

            for (var k = 0; k < LobsterReplay.LevelsInReference; k++)
            {
                var matched = bestByImplementation[0].MatchedByDepth[k];
                var share = bestByImplementation[0].RowsCompared == 0
                    ? 0
                    : matched / (double)bestByImplementation[0].RowsCompared;

                Console.WriteLine($"{k + 1,13} {matched,14:N0} {share,9:P3}");
                depthCurve.Add(new DepthPoint(k + 1, matched, Math.Round(share, 6)));
            }

            Console.WriteLine();

            var first = results[0];
            Console.WriteLine($"Hidden executions (must not move the visible book): {first.HiddenExecutions:N0}");
            Console.WriteLine($"Deltas referencing size resting before the file began: {first.NegativeLevels:N0}");

            if (first.FirstMismatchDetail is not null)
                Console.WriteLine($"First mismatch: {first.FirstMismatchDetail}");

            // Parser throughput on its own, so reconstruction cost can be separated from parse cost.
            var parseRate = MeasureParseThroughput(messages, trials);
            Console.WriteLine();
            Console.WriteLine($"Parser alone: {parseRate.MessagesPerSecond:N0} msg/s, {parseRate.MegabytesPerSecond:N1} MiB/s, {parseRate.BytesPerMessage} B/msg allocated");

            if (outputPath is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath)));
                File.WriteAllText(outputPath, JsonSerializer.Serialize(
                    new
                    {
                        Transitions = transitionReports,
                        Cumulative = results,
                        CumulativeAccuracyByDepth = depthCurve,
                        Parser = parseRate,
                    },
                    new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"Wrote {outputPath}");
            }

            return transitionReports.All(r => r.RowsMatched == r.RowsCompared) ? 0 : 1;
        }

        /// <summary>Price extremes in the reference book, which the seed must be able to represent.</summary>
        private static (int Min, int Max) SurveyReference(ReadOnlySpan<byte> reference)
        {
            var reader = new LobsterReader(reference);
            Span<int> row = stackalloc int[LobsterReplay.LevelsInReference * 4];
            var min = int.MaxValue;
            var max = int.MinValue;

            while (reader.TryReadBookRow(row, out var fields))
            {
                for (var i = 0; i < fields; i += 2)
                {
                    var price = row[i];

                    // Sizes sit in the odd slots; ignore empty levels, whose price is a sentinel.
                    if (row[i + 1] <= 0)
                        continue;

                    if (price < min) min = price;
                    if (price > max) max = price;
                }
            }

            return (min == int.MaxValue ? 0 : min, max == int.MinValue ? 0 : max);
        }

        private static (int Min, int Max, long Count) Survey(ReadOnlySpan<byte> messages)
        {
            var reader = new LobsterReader(messages);
            var min = int.MaxValue;
            var max = int.MinValue;
            var count = 0L;

            while (reader.TryReadMessage(out var message))
            {
                count++;

                if (!message.AffectsVisibleBook)
                    continue;

                if (message.Price < min)
                    min = message.Price;

                if (message.Price > max)
                    max = message.Price;
            }

            return (min, max, count);
        }

        private static ParserReport MeasureParseThroughput(byte[] messages, int trials)
        {
            var best = 0.0;
            long parsed = 0;

            for (var trial = 0; trial < trials; trial++)
            {
                var started = Stopwatch.GetTimestamp();
                var reader = new LobsterReader(messages);
                var checksum = 0L;
                parsed = 0;

                while (reader.TryReadMessage(out var message))
                {
                    // Touch every field so nothing can be optimised away.
                    checksum += message.Price + message.Size + (long)message.Type + message.TimeNanoseconds;
                    parsed++;
                }

                var elapsed = (Stopwatch.GetTimestamp() - started) / (double)Stopwatch.Frequency;

                if (checksum == long.MinValue)
                    throw new InvalidOperationException("unreachable");

                var rate = parsed / elapsed;

                if (rate > best)
                    best = rate;
            }

            // Allocation over a full parse, warmed first.
            var warm = new LobsterReader(messages);
            while (warm.TryReadMessage(out _)) { }

            var before = GC.GetAllocatedBytesForCurrentThread();
            var measured = new LobsterReader(messages);
            long counted = 0;
            while (measured.TryReadMessage(out _)) counted++;
            var bytes = GC.GetAllocatedBytesForCurrentThread() - before;

            return new ParserReport(Math.Round(best, 0),
                Math.Round(best * messages.Length / parsed / 1024 / 1024, 1),
                counted == 0 ? 0 : bytes / counted);
        }

        private record ReplayReport(string Implementation, long MessagesApplied, long RowsCompared,
            long RowsMatched, double MatchRate, double MessagesPerSecond, long FirstMismatchRow,
            string FirstMismatchDetail, long NegativeLevels, long HiddenExecutions);

        private record ParserReport(double MessagesPerSecond, double MegabytesPerSecond, long BytesPerMessage);

        private record DepthPoint(int Levels, long RowsExact, double Share);
    }
}
