using MarketData.Common.Books;
using MarketData.Common.Matching;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace MarketData.Bench
{
    /// <summary>
    /// Micro-benchmark of the matching engine, measured against the size of the resting book.
    /// </summary>
    /// <remarks>
    /// The sweep makes complexity claims falsifiable. Each timed iteration is a state-preserving
    /// two-command cycle, so the resting population is the named independent variable rather than
    /// silently growing between trials.
    /// </remarks>
    public static class MatchingBenchmark
    {
        private const int Operations = 200_000;
        private const int Trials = 5;
        private const int WarmupTrials = 2;
        private const int PriceBand = 2048;

        public static int Run(string[] args)
        {
            var sizes = new[] { 100, 1_000, 10_000, 100_000 };
            string outputPath = null;

            for (var i = 0; i < args.Length; i++)
            {
                if (args[i] == "--sizes")
                    sizes = args[++i].Split(',').Select(v => int.Parse(v, CultureInfo.InvariantCulture)).ToArray();
                else if (args[i] == "--out")
                    outputPath = args[++i];
            }

            Console.WriteLine($"Matching engine micro-benchmark: {Operations:N0} cycles, median of {Trials} trials");
            Console.WriteLine($"Price band +/-{PriceBand}, server GC: {System.Runtime.GCSettings.IsServerGC}");
            Console.WriteLine();
            Console.WriteLine($"{"Resting orders",15} {"add+cancel",12} {"cancel+add",13} {"match+repl",12}");
            Console.WriteLine(new string('-', 57));

            var results = new List<MatchingResult>();

            // The whole sweep runs twice and only the second pass is recorded; see Sweep.
            Sweep(sizes, null);
            Sweep(sizes, results);

            if (outputPath is not null)
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(outputPath)));
                var report = new MatchingReport(DateTimeOffset.UtcNow,
                    RuntimeInformation.FrameworkDescription, RuntimeInformation.OSDescription,
                    RuntimeInformation.ProcessArchitecture.ToString(), Environment.ProcessorCount,
                    System.Runtime.GCSettings.IsServerGC, Operations, Trials, PriceBand, results);
                System.IO.File.WriteAllText(outputPath, JsonSerializer.Serialize(report,
                    new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"Wrote {outputPath}");
            }

            return 0;
        }

        /// <summary>Builds a book of <paramref name="size"/> resting orders, well away from the touch.</summary>
        private static LimitOrderBook Populate(int size, Random random, List<ulong> ids, out ulong nextId)
        {
            var book = new LimitOrderBook(-PriceBand, PriceBand);
            nextId = 1;
            ids.Clear();

            for (var i = 0; i < size; i++)
            {
                // Bids strictly below zero and asks strictly above, so seeding never crosses and
                // the book under test is purely resting depth.
                var side = (i & 1) == 0 ? Side.Bid : Side.Ask;
                var price = side == Side.Bid ? -random.Next(1, PriceBand) : random.Next(1, PriceBand);
                var id = nextId++;

                book.Submit(id, side, OrderType.Limit, TimeInForce.GoodTilCancel, price, (uint)random.Next(1, 500), null);
                ids.Add(id);
            }

            return book;
        }

        /// <summary>
        /// Runs the sweep, recording into <paramref name="results"/> when it is not null.
        /// </summary>
        /// <remarks>
        /// Called twice, and the first pass is discarded. The per-measurement warm-up trials are
        /// not enough on their own: promotion to optimised code happens on a background thread, so
        /// the first configuration measured can finish while still executing unoptimised code. It
        /// showed up as the smallest book appearing twice as slow per operation as books ten times
        /// its size - not a cache effect and not the algorithm, but the first row paying for the
        /// JIT on everyone's behalf. Warm-up trials cannot fix that, because they do not buy
        /// wall-clock time for a compilation to land.
        /// </remarks>
        private static void Sweep(int[] sizes, List<MatchingResult> results)
        {
            foreach (var size in sizes)
            {
                var add = MeasureAdd(size);
                var cancel = MeasureCancel(size);
                var match = MeasureMatch(size);
                if (results is null)
                    continue;

                Console.WriteLine($"{size,15:N0} {add.Median,12:F1} {cancel.Median,13:F1} {match.Median,12:F1}");
                results.Add(new MatchingResult(size, add, cancel, match));
            }
        }

        private static Measurement MeasureAdd(int size)
        {
            var random = new Random(7);
            var ids = new List<ulong>();
            var book = Populate(size, random, ids, out var nextId);

            // Prices are drawn up front so the timed loop measures the book, not the generator.
            var prices = new int[Operations];
            var sides = new Side[Operations];

            for (var i = 0; i < Operations; i++)
            {
                sides[i] = (i & 1) == 0 ? Side.Bid : Side.Ask;
                prices[i] = sides[i] == Side.Bid ? -random.Next(1, PriceBand) : random.Next(1, PriceBand);
            }

            return Measure(() =>
            {
                var id = nextId;

                for (var i = 0; i < Operations; i++)
                {
                    book.Submit(id++, sides[i], OrderType.Limit, TimeInForce.GoodTilCancel, prices[i], 100, null);

                    // Cancel it straight back out, so the book stays at the size under test rather
                    // than growing and turning this into a memory benchmark.
                    book.Cancel(id - 1, null);
                }

                nextId = id;
                return book.OrderCount;
            }, Operations);
        }

        /// <summary>
        /// Cancel-plus-replacement cost as a function of book size, with price-level churn held out.
        /// </summary>
        /// <remarks>
        /// Every order is placed on one of a small fixed set of price levels, so the cycle measures
        /// hash lookup, intrusive unlink, and replacement without price-level creation/removal.
        /// </remarks>
        private static Measurement MeasureCancel(int size)
        {
            const int levels = 64;

            var random = new Random(11);
            var book = new LimitOrderBook(-PriceBand, PriceBand);
            var ids = new List<ulong>(size);
            ulong nextId = 1;

            for (var i = 0; i < size; i++)
            {
                var side = (i & 1) == 0 ? Side.Bid : Side.Ask;
                var price = side == Side.Bid ? -1 - random.Next(levels) : 1 + random.Next(levels);
                var id = nextId++;

                book.Submit(id, side, OrderType.Limit, TimeInForce.GoodTilCancel, price, 100, null);
                ids.Add(id);
            }

            var sides = new Side[Operations];
            var prices = new int[Operations];

            for (var i = 0; i < Operations; i++)
            {
                sides[i] = (i & 1) == 0 ? Side.Bid : Side.Ask;
                prices[i] = sides[i] == Side.Bid ? -1 - random.Next(levels) : 1 + random.Next(levels);
            }

            return Measure(() =>
            {
                var id = nextId;

                for (var i = 0; i < Operations; i++)
                {
                    // Cancel one, add one: book size is held exactly constant, so the only variable
                    // across rows of the table is how many orders are resting.
                    var victimIndex = i % ids.Count;

                    if (!book.Cancel(ids[victimIndex], null))
                        throw new InvalidOperationException("benchmark lost a live order id");

                    book.Submit(id, sides[i], OrderType.Limit, TimeInForce.GoodTilCancel, prices[i], 100, null);
                    ids[victimIndex] = id++;
                }

                nextId = id;
                return book.OrderCount;
            }, Operations);
        }

        private static Measurement MeasureMatch(int size)
        {
            var book = new LimitOrderBook(-PriceBand, PriceBand);
            ulong nextId = 1;

            for (var i = 0; i < size; i++)
            {
                var side = (i & 1) == 0 ? Side.Bid : Side.Ask;
                book.Submit(nextId++, side, OrderType.Limit, TimeInForce.GoodTilCancel,
                    side == Side.Bid ? -1 : 1, 50, null);
            }

            var events = new List<MarketEvent>(64);

            return Measure(() =>
            {
                var id = nextId;

                for (var i = 0; i < Operations; i++)
                {
                    events.Clear();

                    // Every resting order is exactly the market order size. One order leaves and
                    // one returns at the same price, preserving count, quantity, and touch.
                    var side = (i & 1) == 0 ? Side.Bid : Side.Ask;
                    book.Submit(id++, side, OrderType.Market, TimeInForce.ImmediateOrCancel, 0, 50, events);

                    var opposite = side == Side.Bid ? Side.Ask : Side.Bid;
                    book.Submit(id++, opposite, OrderType.Limit, TimeInForce.GoodTilCancel,
                        opposite == Side.Bid ? -1 : 1, 50, null);
                }

                if (book.OrderCount != size)
                    throw new InvalidOperationException("match cycle changed the resting population");

                nextId = id;
                return book.OrderCount;
            }, Operations);
        }

        private static Measurement Measure(Func<int> body, int operations)
        {
            for (var i = 0; i < WarmupTrials; i++)
                Consume(body());

            var samples = new double[Trials];

            for (var trial = 0; trial < Trials; trial++)
            {
                var start = Stopwatch.GetTimestamp();
                Consume(body());
                var elapsed = Stopwatch.GetTimestamp() - start;

                samples[trial] = elapsed * (1_000_000_000.0 / Stopwatch.Frequency) / operations;
            }

            Array.Sort(samples);
            return new Measurement(Math.Round(samples[Trials / 2], 2),
                Math.Round(samples[0], 2), Math.Round(samples[^1], 2));
        }

        private static void Consume(int value)
        {
            if (value == int.MinValue)
                throw new InvalidOperationException("unreachable; defeats dead-code elimination");
        }

        private sealed record Measurement(double Median, double Min, double Max);
        private sealed record MatchingResult(int RestingOrders, Measurement AddCancelCycleNs,
            Measurement CancelAddCycleNs, Measurement MatchReplenishCycleNs);

        private sealed record MatchingReport(DateTimeOffset TimestampUtc, string Runtime,
            string OperatingSystem, string Architecture, int LogicalProcessors, bool ServerGc,
            int CyclesPerTrial, int Trials, int PriceBand, List<MatchingResult> Results);
    }
}
