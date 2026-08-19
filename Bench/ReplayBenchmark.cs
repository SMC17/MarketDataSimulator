using MarketData.Common.Books;
using MarketData.Common.Lobster;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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
            var warmup = 1;
            var trials = 5;
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

            if (warmup < 0 || trials < 3)
                throw new ArgumentOutOfRangeException(nameof(args), "warmup must be non-negative and trials at least three");

            var sessions = LobsterSessions.Discover(directory);

            if (sessions.Count == 0)
            {
                Console.Error.WriteLine($"No LOBSTER message/orderbook pairs found in '{directory}'.");
                Console.Error.WriteLine("Run scripts/fetch-lobster.sh first.");
                return 2;
            }

            var allExact = true;

            foreach (var session in sessions)
            {
                if (!RunSession(session, warmup, trials, outputPath))
                    allExact = false;
            }

            return allExact ? 0 : 1;
        }

        private static bool RunSession(LobsterSession session, int warmup, int trials, string outputPath)
        {
            var messagePath = session.MessagePath;
            var referencePath = session.ReferencePath;

            Console.WriteLine();
            Console.WriteLine(new string('=', 92));
            Console.WriteLine($"{session}");
            Console.WriteLine(new string('=', 92));
            var messages = LobsterSessions.ReadAllBytes(messagePath);
            var reference = LobsterSessions.ReadAllBytes(referencePath);

            var (minPrice, maxPrice, count) = Survey(messages);
            var levels = LobsterReplay.DetectLevels(reference);
            var (referenceMin, referenceMax) = SurveyReference(reference);
            minPrice = Math.Min(minPrice, referenceMin);
            maxPrice = Math.Max(maxPrice, referenceMax);
            Console.WriteLine($"Messages : {count:N0}   price band [{minPrice:N0}, {maxPrice:N0}] " +
                              $"({(maxPrice - minPrice) / 10000.0:F2} dollars)");
            Console.WriteLine($"Seed after: {Math.Max(1, warmup):N0} message(s); throughput is median of {trials} trials");
            Console.WriteLine($"Depth    : {levels} levels published per side");
            Console.WriteLine();

            // Padded so a level can never fall outside the ladder's band.
            var band = 10_000;
            var results = new List<ReplayReport>();
            var representativeByImplementation = new List<ReplayResult>();

            Console.WriteLine("SINGLE-STEP TRANSITIONS - seed from the exchange's published book, apply one");
            Console.WriteLine("message, compare against the exchange's next published book.");
            Console.WriteLine();
            Console.WriteLine($"{"Implementation",18} {"Verified",14} {"Exact",14} {"Accuracy",11} {"Msgs/s",12} {"Unverifiable",14}");
            Console.WriteLine(new string('-', 92));

            var transitionReports = new List<ReplayReport>();

            foreach (var name in new[] { "SortedArray", "Vectorized", "Ladder", "Tree" })
            {
                var trialResults = new List<ReplayResult>(trials);
                var warmBook = name == "Ladder"
                    ? new LadderBook(4096, minPrice - 10_000, maxPrice + 10_000)
                    : BookFactory.Create(name, 4096, maxPrice + 10_000);
                LobsterReplay.ReplayTransitions(messages, reference, warmBook);

                for (var trial = 0; trial < trials; trial++)
                {
                    var book = name == "Ladder"
                        ? new LadderBook(4096, minPrice - 10_000, maxPrice + 10_000)
                        : BookFactory.Create(name, 4096, maxPrice + 10_000);

                    trialResults.Add(LobsterReplay.ReplayTransitions(messages, reference, book));
                }

                var measured = MedianByRate(trialResults);
                var (minimumRate, maximumRate) = RateRange(trialResults);

                Console.WriteLine($"{name,18} {measured.RowsCompared,14:N0} {measured.RowsMatched,14:N0} " +
                                  $"{measured.MatchRate,10:P4} {measured.MessagesPerSecond,12:N0} {measured.Unverifiable,14:N0}");

                transitionReports.Add(new ReplayReport(name, measured.MessagesApplied, measured.RowsCompared,
                    measured.RowsMatched, Math.Round(measured.MatchRate, 6), Math.Round(measured.MessagesPerSecond, 0),
                    Math.Round(minimumRate, 0), Math.Round(maximumRate, 0), measured.Unverifiable,
                    measured.FirstMismatchRow, measured.FirstMismatchDetail, measured.NegativeLevels,
                    measured.HiddenExecutions));
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
                var trialResults = new List<ReplayResult>(trials);
                var warmBook = name == "Ladder"
                    ? new LadderBook(4096, minPrice - band, maxPrice + band)
                    : BookFactory.Create(name, 4096, maxPrice + band);
                LobsterReplay.Replay(messages, reference, warmBook, warmup);

                for (var trial = 0; trial < trials; trial++)
                {
                    // Depth well beyond the ten levels compared, so the book is a full book and the
                    // comparison is not quietly helped by truncation.
                    var book = name == "Ladder"
                        ? new LadderBook(4096, minPrice - band, maxPrice + band)
                        : BookFactory.Create(name, 4096, maxPrice + band);

                    trialResults.Add(LobsterReplay.Replay(messages, reference, book, warmup));
                }

                var measured = MedianByRate(trialResults);
                var (minimumRate, maximumRate) = RateRange(trialResults);

                var mismatch = measured.FirstMismatchRow < 0 ? "none" : measured.FirstMismatchRow.ToString("N0");
                Console.WriteLine($"{name,18} {measured.RowsCompared,14:N0} {measured.RowsMatched,14:N0} " +
                                  $"{measured.MatchRate,10:P4} {measured.MessagesPerSecond,12:N0} {mismatch,15}");

                representativeByImplementation.Add(measured);
                results.Add(new ReplayReport(name, measured.MessagesApplied, measured.RowsCompared, measured.RowsMatched,
                    Math.Round(measured.MatchRate, 6), Math.Round(measured.MessagesPerSecond, 0),
                    Math.Round(minimumRate, 0), Math.Round(maximumRate, 0), measured.Unverifiable,
                    measured.FirstMismatchRow, measured.FirstMismatchDetail, measured.NegativeLevels,
                    measured.HiddenExecutions));
            }

            Console.WriteLine();
            Console.WriteLine("Reconstruction accuracy by depth (share of rows whose top-k levels match exactly):");
            Console.WriteLine();
            Console.WriteLine($"{"Top-k levels",13} {"Rows exact",14} {"Share",10}");
            Console.WriteLine(new string('-', 40));

            var depthCurve = new List<DepthPoint>();

            for (var k = 0; k < levels; k++)
            {
                var matched = representativeByImplementation[0].MatchedByDepth[k];
                var share = representativeByImplementation[0].RowsCompared == 0
                    ? 0
                    : matched / (double)representativeByImplementation[0].RowsCompared;

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
            Console.WriteLine($"Parser alone: {parseRate.MessagesPerSecond:N0} msg/s, {parseRate.MebibytesPerSecond:N1} MiB/s, {parseRate.BytesPerMessage} B/msg allocated");

            if (outputPath is not null)
            {
                var full = Path.GetFullPath(outputPath);
                var perSession = Path.Combine(Path.GetDirectoryName(full),
                    $"{Path.GetFileNameWithoutExtension(full)}-{session.Symbol}{Path.GetExtension(full)}");

                Directory.CreateDirectory(Path.GetDirectoryName(full));
                File.WriteAllText(perSession, JsonSerializer.Serialize(
                    new
                    {
                        TimestampUtc = DateTimeOffset.UtcNow,
                        Runtime = RuntimeInformation.FrameworkDescription,
                        OperatingSystem = RuntimeInformation.OSDescription,
                        Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                        LogicalProcessors = Environment.ProcessorCount,
                        ServerGc = System.Runtime.GCSettings.IsServerGC,
                        Trials = trials,
                        SeedAfterMessages = Math.Max(1, warmup),
                        Session = session.ToString(),
                        Symbol = session.Symbol,
                        Levels = session.Levels,
                        Transitions = transitionReports,
                        Cumulative = results,
                        CumulativeAccuracyByDepth = depthCurve,
                        Parser = parseRate,
                    },
                    new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"Wrote {perSession}");
            }

            return transitionReports.All(r => r.RowsMatched == r.RowsCompared);
        }

        /// <summary>Price extremes in the reference book, which the seed must be able to represent.</summary>
        private static (int Min, int Max) SurveyReference(ReadOnlySpan<byte> reference)
        {
            var reader = new LobsterReader(reference);
            Span<int> row = stackalloc int[LobsterReplay.MaxLevels * 4];
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
            var rates = new double[trials];
            long parsed = 0;
            var warm = new LobsterReader(messages);
            while (warm.TryReadMessage(out _)) { }

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

                rates[trial] = parsed / elapsed;
            }

            Array.Sort(rates);
            var median = rates[trials / 2];

            // Allocation over a full parse after the timed passes have warmed the path.
            var before = GC.GetAllocatedBytesForCurrentThread();
            var measured = new LobsterReader(messages);
            long counted = 0;
            while (measured.TryReadMessage(out _)) counted++;
            var bytes = GC.GetAllocatedBytesForCurrentThread() - before;

            return new ParserReport(Math.Round(median, 0), Math.Round(rates[0], 0),
                Math.Round(rates[^1], 0),
                Math.Round(median * messages.Length / parsed / 1024 / 1024, 1),
                counted == 0 ? 0 : bytes / counted);
        }

        private static ReplayResult MedianByRate(List<ReplayResult> results)
        {
            results.Sort((left, right) => left.MessagesPerSecond.CompareTo(right.MessagesPerSecond));
            return results[results.Count / 2];
        }

        private static (double Minimum, double Maximum) RateRange(List<ReplayResult> results)
            => (results[0].MessagesPerSecond, results[^1].MessagesPerSecond);

        private record ReplayReport(string Implementation, long MessagesApplied, long RowsCompared,
            long RowsMatched, double MatchRate, double MessagesPerSecond,
            double MinMessagesPerSecond, double MaxMessagesPerSecond, long Unverifiable,
            long FirstMismatchRow, string FirstMismatchDetail, long NegativeLevels,
            long HiddenExecutions);

        private record ParserReport(double MessagesPerSecond, double MinMessagesPerSecond,
            double MaxMessagesPerSecond, double MebibytesPerSecond, long BytesPerMessage);

        private record DepthPoint(int Levels, long RowsExact, double Share);
    }
}
