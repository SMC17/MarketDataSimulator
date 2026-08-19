using MarketData.Common.Analytics;
using MarketData.Common.Lobster;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MarketData.Bench
{
    /// <summary>
    /// Measures whether order flow imbalance predicts price on a real NASDAQ session.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The point of reconstructing a book is to compute something from it, and the canonical
    /// something is order flow imbalance. Cont, Kukanov and Stoikov (2014) showed that net
    /// pressure at the touch explains contemporaneous price moves close to linearly across US
    /// equities. This runs that test on the AMZN session in this repository.
    /// </para>
    /// <para>
    /// Quotes are taken from the exchange's own published book rather than from a reconstruction,
    /// so the study measures the relationship in the data and not the fidelity of this project's
    /// replay - which the transition test already established separately.
    /// </para>
    /// <para>
    /// Events are bucketed into fixed-size blocks and non-overlapping, which matters: overlapping
    /// windows share observations, and the resulting autocorrelation inflates significance for
    /// free. The horizon sweep exists because a signal that explains the current move and a signal
    /// that predicts the next one are very different claims, and only the second is tradeable.
    /// </para>
    /// </remarks>
    public static class MicrostructureStudy
    {
        public static int Run(string[] args)
        {
            var directory = "data/lobster";
            var buckets = new[] { 10, 25, 50, 100, 250, 500 };
            string outputPath = null;

            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--data": directory = args[++i]; break;
                    case "--buckets": buckets = args[++i].Split(',').Select(v => int.Parse(v, CultureInfo.InvariantCulture)).ToArray(); break;
                    case "--out": outputPath = args[++i]; break;
                }
            }

            var sessions = LobsterSessions.Discover(directory);

            if (sessions.Count == 0)
            {
                Console.Error.WriteLine($"No LOBSTER sessions in '{directory}'. Run scripts/fetch-lobster.sh.");
                return 2;
            }

            var all = new List<SessionResult>();

            foreach (var session in sessions)
            {
                var reference = File.ReadAllBytes(session.ReferencePath);
                var quotes = LoadQuotes(reference);
                var twoSided = quotes.Where(q => q.IsTwoSided).ToArray();

                if (twoSided.Length == 0)
                    continue;

                var spreads = twoSided.Select(q => (double)q.Spread).ToArray();
                var midDollars = twoSided.Select(q => q.MidHalfTicks / 2.0 / 10000.0).ToArray();
                var meanSpread = spreads.Average() / 10000.0;
                var meanMid = midDollars.Average();

                Console.WriteLine();
                Console.WriteLine(new string('=', 92));
                Console.WriteLine($"{session}   {quotes.Count:N0} quotes   " +
                                  $"mid ~${meanMid:F2}   spread {meanSpread:F4} " +
                                  $"({meanSpread / meanMid * 10000:F1} bp, {meanSpread / 0.01:F1} ticks)");
                Console.WriteLine(new string('=', 92));
                Console.WriteLine();

                Console.WriteLine($"{"Bucket",8} {"Samples",9} {"Contemp R2",12} {"slope",10} {"t",9}   " +
                                  $"{"Predict R2",11} {"slope",10} {"t",9}");
                Console.WriteLine(new string('-', 92));

                var results = new List<BucketResult>();

                foreach (var size in buckets)
                {
                    var (contemporaneous, predictive) = Study(quotes, size);

                    Console.WriteLine($"{size,8} {contemporaneous.Count,9:N0} {contemporaneous.RSquared,11:P2} " +
                                      $"{contemporaneous.Slope,10:F5} {contemporaneous.SlopeTStatistic,9:F1}   " +
                                      $"{predictive.RSquared,10:P2} {predictive.Slope,10:F5} {predictive.SlopeTStatistic,9:F1}");

                    results.Add(new BucketResult(size, contemporaneous.Count,
                        Math.Round(contemporaneous.RSquared, 6), Math.Round(contemporaneous.Slope, 8),
                        Math.Round(contemporaneous.SlopeTStatistic, 2),
                        Math.Round(predictive.RSquared, 6), Math.Round(predictive.Slope, 8),
                        Math.Round(predictive.SlopeTStatistic, 2)));
                }

                all.Add(new SessionResult(session.Symbol, session.ToString(), quotes.Count,
                    Math.Round(meanMid, 2), Math.Round(meanSpread, 5),
                    Math.Round(meanSpread / meanMid * 10000, 2), Math.Round(meanSpread / 0.01, 2), results));
            }

            Console.WriteLine();
            Console.WriteLine("Slope is half-ticks of mid change per unit of imbalance; a $0.0001 price unit");
            Console.WriteLine("means 2 half-ticks = $0.0001. Positive slope: buying pressure raises the price.");
            Console.WriteLine("Buckets are non-overlapping; predictive uses the PREVIOUS bucket's imbalance.");

            if (outputPath is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath)));
                File.WriteAllText(outputPath, JsonSerializer.Serialize(all,
                    new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"Wrote {outputPath}");
            }

            return 0;
        }

        private static (OnlineRegression Contemporaneous, OnlineRegression Predictive) Study(
            List<Quote> quotes, int bucketSize)
        {
            var monitor = new MicrostructureMonitor();
            var contemporaneous = new OnlineRegression();
            var predictive = new OnlineRegression();

            long bucketStartMid = 0;
            var haveBucketStart = false;
            var index = 0;

            double? previousFlow = null;

            foreach (var quote in quotes)
            {
                if (!quote.IsTwoSided)
                    continue;

                monitor.Update(quote);

                if (!haveBucketStart)
                {
                    bucketStartMid = quote.MidHalfTicks;
                    haveBucketStart = true;
                    index = 0;
                    continue;
                }

                if (++index < bucketSize)
                    continue;

                var flow = (double)monitor.ResetFlow();
                var change = (double)(quote.MidHalfTicks - bucketStartMid);

                contemporaneous.Add(flow, change);

                // The previous bucket's flow against this bucket's move: strictly out of sample in
                // time, which is the only version of the claim that could be traded on.
                if (previousFlow.HasValue)
                    predictive.Add(previousFlow.Value, change);

                previousFlow = flow;
                bucketStartMid = quote.MidHalfTicks;
                index = 0;
            }

            return (contemporaneous, predictive);
        }

        private static (OnlineRegression Contemporaneous, OnlineRegression Predictive) Study(
            Quote[] quotes, int bucketSize) => Study(quotes.ToList(), bucketSize);

        private static List<Quote> LoadQuotes(ReadOnlySpan<byte> reference)
        {
            var reader = new LobsterReader(reference);
            Span<int> row = stackalloc int[LobsterReplay.MaxLevels * 4];
            var quotes = new List<Quote>(300_000);

            while (reader.TryReadBookRow(row, out var fields) && fields >= 4)
                quotes.Add(new Quote(row[2], (uint)Math.Max(0, row[3]), row[0], (uint)Math.Max(0, row[1])));

            return quotes;
        }

        private static double Median(double[] values)
        {
            var sorted = (double[])values.Clone();
            Array.Sort(sorted);
            return sorted.Length == 0 ? 0 : sorted[sorted.Length / 2];
        }

        private record SessionResult(string Symbol, string Session, int Quotes, double MeanMid,
            double MeanSpreadDollars, double MeanSpreadBasisPoints, double MeanSpreadTicks,
            List<BucketResult> Buckets);

        private record BucketResult(int BucketEvents, long Samples,
            double ContemporaneousRSquared, double ContemporaneousSlope, double ContemporaneousT,
            double PredictiveRSquared, double PredictiveSlope, double PredictiveT);
    }
}
