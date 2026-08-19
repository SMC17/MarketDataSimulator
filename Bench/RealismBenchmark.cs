using MarketData.Common.Analytics;
using MarketData.Common.Books;
using MarketData.Common.Lobster;
using MarketData.Common.Matching;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace MarketData.Bench
{
    /// <summary>
    /// Compares the simulator's output against real NASDAQ sessions on the statistics that
    /// distinguish a market from a random walk.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The project has both halves needed for this: a matching engine driven by generated order
    /// flow, and real sessions from three instruments. Holding one against the other is the only
    /// way to know whether the simulator produces a market or merely produces messages.
    /// </para>
    /// <para>
    /// The expectation is that it does not fully match, and saying where is the point. A generator
    /// with independent arrivals cannot manufacture volatility clustering, because clustering comes
    /// from the arrival process being self-exciting - one trade making the next more likely. This
    /// measures the size of that gap instead of asserting realism.
    /// </para>
    /// </remarks>
    public static class RealismBenchmark
    {
        public static int Run(string[] args)
        {
            var directory = "data/lobster";
            var simulatedEvents = 400_000;
            var seed = 20260819;
            string outputPath = null;

            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--data": directory = args[++i]; break;
                    case "--events": simulatedEvents = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--seed": seed = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--out": outputPath = args[++i]; break;
                }
            }

            var rows = new List<Row>();

            foreach (var session in LobsterSessions.Discover(directory))
                rows.Add(Measure(session.Symbol + " (real)", RealMidPrices(session.ReferencePath)));

            rows.Add(Measure("simulator", SimulatedMidPrices(simulatedEvents, seed)));

            if (rows.Count == 1)
                Console.WriteLine("No real sessions found; run scripts/fetch-lobster.sh to compare against them.");

            Console.WriteLine();
            Console.WriteLine("Stylized facts of mid-price returns, sampled every 20 book updates.");
            Console.WriteLine("A random walk scores ~0 excess kurtosis and ~0 volatility clustering.");
            Console.WriteLine();
            Console.WriteLine($"{"Series",18} {"Obs",9} {"ExKurtosis",12} {"|r| ac(1)",11} {"|r| ac(10)",11} " +
                              $"{"r ac(1)",10} {">3 sigma",10} {">5 sigma",10}");
            Console.WriteLine(new string('-', 96));

            foreach (var row in rows)
            {
                Console.WriteLine($"{row.Name,18} {row.Observations,9:N0} {row.ExcessKurtosis,12:F2} " +
                                  $"{row.AbsAutocorrelation1,11:F4} {row.AbsAutocorrelation10,11:F4} " +
                                  $"{row.ReturnAutocorrelation1,10:F4} {row.Beyond3Sigma,10:P3} {row.Beyond5Sigma,10:P3}");
            }

            Console.WriteLine();
            Console.WriteLine("Normal reference: excess kurtosis 0, >3 sigma 0.270%, >5 sigma 0.00006%.");

            if (outputPath is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath)));
                File.WriteAllText(outputPath, JsonSerializer.Serialize(rows,
                    new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"Wrote {outputPath}");
            }

            return 0;
        }

        /// <summary>Sampling stride, in book updates, between the mid prices a return is taken from.</summary>
        private const int Stride = 20;

        private static Row Measure(string name, IEnumerable<double> mids)
        {
            var facts = new StylizedFacts();
            double? previous = null;
            var index = 0;

            foreach (var mid in mids)
            {
                if (index++ % Stride != 0)
                    continue;

                if (previous.HasValue && previous.Value > 0)
                {
                    // Log returns, so a move is measured in proportion rather than in dollars and
                    // instruments at $30 and $570 are directly comparable.
                    facts.AddReturn(Math.Log(mid / previous.Value));
                }

                previous = mid;
            }

            return new Row(name, facts.Observations,
                Math.Round(facts.ExcessKurtosis, 3),
                Math.Round(facts.AbsoluteReturnAutocorrelation(1), 5),
                Math.Round(facts.AbsoluteReturnAutocorrelation(10), 5),
                Math.Round(facts.ReturnAutocorrelation(1), 5),
                Math.Round(facts.TailFraction(3), 6),
                Math.Round(facts.TailFraction(5), 6));
        }

        private static IEnumerable<double> RealMidPrices(string referencePath)
        {
            var reference = File.ReadAllBytes(referencePath);
            var mids = new List<double>(300_000);
            CollectRealMids(reference, mids);
            return mids;
        }

        // A ref struct reader cannot live across a yield, so the rows are collected eagerly here.
        private static void CollectRealMids(ReadOnlySpan<byte> reference, List<double> mids)
        {
            var reader = new LobsterReader(reference);
            Span<int> row = stackalloc int[LobsterReplay.MaxLevels * 4];

            while (reader.TryReadBookRow(row, out var fields) && fields >= 4)
            {
                if (row[1] <= 0 || row[3] <= 0)
                    continue;

                mids.Add((row[0] + row[2]) / 2.0);
            }
        }

        private static IEnumerable<double> SimulatedMidPrices(int events, int seed)
        {
            const int band = 4096;

            var book = new LimitOrderBook(-band, band);
            var flow = new OrderFlowSimulator(book);
            var random = new Random(seed);
            var marketEvents = new List<MarketEvent>(64);
            var mids = new List<double>(events);

            for (var i = 0; i < events; i++)
            {
                marketEvents.Clear();
                flow.Step(random, marketEvents);

                if (i % 4096 == 0)
                    flow.Compact();

                if (!book.TryGetBest(Side.Bid, out var bid, out _) ||
                    !book.TryGetBest(Side.Ask, out var ask, out _))
                {
                    continue;
                }

                // Offset into positive territory so a log return is defined; the simulator's price
                // axis is centred on zero and only relative moves are being compared.
                mids.Add((bid + ask) / 2.0 + band * 2);
            }

            return mids;
        }

        private record Row(string Name, long Observations, double ExcessKurtosis,
            double AbsAutocorrelation1, double AbsAutocorrelation10, double ReturnAutocorrelation1,
            double Beyond3Sigma, double Beyond5Sigma);
    }
}
