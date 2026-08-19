using MarketData.Common.Books;
using MarketData.Common.Matching;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace MarketData.Bench
{
    /// <summary>
    /// Micro-benchmark of the matching engine, measured against the size of the resting book.
    /// </summary>
    /// <remarks>
    /// The point of the sweep across book sizes is to make the complexity claims falsifiable. An
    /// O(1) cancel should cost the same with a hundred resting orders as with a hundred thousand;
    /// if the measured cost climbs with book size, the claim is wrong regardless of what the code
    /// looks like. Real venues receive far more cancels than anything else, so that column is the
    /// one that decides whether the structure was worth building.
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

            Console.WriteLine($"Matching engine micro-benchmark: {Operations:N0} operations, {Trials} trials, minimum reported");
            Console.WriteLine($"Price band +/-{PriceBand}, server GC: {System.Runtime.GCSettings.IsServerGC}");
            Console.WriteLine();
            Console.WriteLine($"{"Resting orders",15} {"Add ns/op",11} {"Cancel ns/op",13} {"Match ns/op",12} {"Mixed ns/op",12} {"Mixed B/op",11}");
            Console.WriteLine(new string('-', 80));

            var results = new List<MatchingResult>();

            // The whole sweep runs twice and only the second pass is recorded; see Sweep.
            Sweep(sizes, null);
            Sweep(sizes, results);

            Console.WriteLine();
            Console.WriteLine("Mixed = 60% add, 35% cancel, 5% aggressive, approximating a real venue's message mix.");

            if (outputPath is not null)
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(outputPath)));
                System.IO.File.WriteAllText(outputPath, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
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
                var mixed = MeasureMixed(size);
                var bytes = MeasureMixedAllocation(size);

                if (results is null)
                    continue;

                Console.WriteLine($"{size,15:N0} {add,11:F1} {cancel,13:F1} {match,12:F1} {mixed,12:F1} {bytes,11:F1}");
                results.Add(new MatchingResult(size, Math.Round(add, 2), Math.Round(cancel, 2),
                    Math.Round(match, 2), Math.Round(mixed, 2), Math.Round(bytes, 1)));
            }
        }

        private static double MeasureAdd(int size)
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
        /// Cancel cost as a function of book size, with price-level churn held out.
        /// </summary>
        /// <remarks>
        /// Every order is placed on one of a small fixed set of price levels, so a cancel never
        /// empties a level and never creates one. What remains is exactly the O(1) claim: one hash
        /// lookup to find the order, and one unlink from its queue. If this column climbs with
        /// book size, the cost is coming from somewhere other than the algorithm.
        /// </remarks>
        private static double MeasureCancel(int size)
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

            var victims = new ulong[Operations];

            for (var i = 0; i < Operations; i++)
                victims[i] = ids[random.Next(ids.Count)];

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
                    book.Cancel(victims[i], null);
                    book.Submit(id, sides[i], OrderType.Limit, TimeInForce.GoodTilCancel, prices[i], 100, null);
                    victims[i] = id++;
                }

                nextId = id;
                return book.OrderCount;
            }, Operations);
        }

        private static double MeasureMatch(int size)
        {
            var random = new Random(13);
            var ids = new List<ulong>();
            var book = Populate(size, random, ids, out var nextId);
            var events = new List<MarketEvent>(64);

            return Measure(() =>
            {
                var id = nextId;

                for (var i = 0; i < Operations; i++)
                {
                    events.Clear();

                    // Aggress at the touch, then replenish the other side so the book does not
                    // drain away over the run.
                    var side = (i & 1) == 0 ? Side.Bid : Side.Ask;
                    book.Submit(id++, side, OrderType.Market, TimeInForce.ImmediateOrCancel, 0, 50, events);

                    var opposite = side == Side.Bid ? Side.Ask : Side.Bid;
                    var price = opposite == Side.Bid ? -random.Next(1, 50) : random.Next(1, 50);
                    book.Submit(id++, opposite, OrderType.Limit, TimeInForce.GoodTilCancel, price, 50, null);
                }

                nextId = id;
                return events.Count;
            }, Operations);
        }

        private static double MeasureMixed(int size)
        {
            var random = new Random(17);
            var ids = new List<ulong>();
            var book = Populate(size, random, ids, out var nextId);
            var script = BuildMixedScript(random);
            var events = new List<MarketEvent>(64);

            return Measure(() =>
            {
                var id = nextId;
                var resting = new Queue<ulong>(ids);

                for (var i = 0; i < script.Length; i++)
                {
                    var step = script[i];

                    switch (step.Kind)
                    {
                        case 0:
                            book.Submit(id, step.Side, OrderType.Limit, TimeInForce.GoodTilCancel, step.Price, 100, null);
                            resting.Enqueue(id++);
                            break;
                        case 1:
                            if (resting.Count > 0)
                                book.Cancel(resting.Dequeue(), null);
                            break;
                        default:
                            events.Clear();
                            book.Submit(id++, step.Side, OrderType.Market, TimeInForce.ImmediateOrCancel, 0, 50, events);
                            break;
                    }
                }

                nextId = id;
                return book.OrderCount;
            }, script.Length);
        }

        private static double MeasureMixedAllocation(int size)
        {
            var random = new Random(19);
            var ids = new List<ulong>();
            var book = Populate(size, random, ids, out var nextId);
            var script = BuildMixedScript(random);
            var events = new List<MarketEvent>(64);
            var resting = new Queue<ulong>(ids);
            var id = nextId;

            // Warm up so lazy growth of the id map and level pool is not charged per operation.
            for (var i = 0; i < 20_000 && i < script.Length; i++)
                Apply(book, script[i], ref id, resting, events);

            var before = GC.GetAllocatedBytesForCurrentThread();
            var counted = 0;

            for (var i = 20_000; i < script.Length; i++)
            {
                Apply(book, script[i], ref id, resting, events);
                counted++;
            }

            return counted == 0 ? 0 : (GC.GetAllocatedBytesForCurrentThread() - before) / (double)counted;
        }

        private static void Apply(LimitOrderBook book, Step step, ref ulong id, Queue<ulong> resting, List<MarketEvent> events)
        {
            switch (step.Kind)
            {
                case 0:
                    book.Submit(id, step.Side, OrderType.Limit, TimeInForce.GoodTilCancel, step.Price, 100, null);
                    resting.Enqueue(id++);
                    break;
                case 1:
                    if (resting.Count > 0)
                        book.Cancel(resting.Dequeue(), null);
                    break;
                default:
                    events.Clear();
                    book.Submit(id++, step.Side, OrderType.Market, TimeInForce.ImmediateOrCancel, 0, 50, events);
                    break;
            }
        }

        private readonly record struct Step(byte Kind, Side Side, int Price);

        /// <summary>60% add, 35% cancel, 5% aggressive - roughly a real venue's message mix.</summary>
        private static Step[] BuildMixedScript(Random random)
        {
            var script = new Step[Operations];

            for (var i = 0; i < Operations; i++)
            {
                var side = random.Next(2) == 0 ? Side.Bid : Side.Ask;
                var price = side == Side.Bid ? -random.Next(1, PriceBand) : random.Next(1, PriceBand);
                var roll = random.NextDouble();
                var kind = roll < 0.60 ? (byte)0 : roll < 0.95 ? (byte)1 : (byte)2;
                script[i] = new Step(kind, side, price);
            }

            return script;
        }

        private static double Measure(Func<int> body, int operations)
        {
            for (var i = 0; i < WarmupTrials; i++)
                Consume(body());

            var best = double.MaxValue;

            for (var trial = 0; trial < Trials; trial++)
            {
                var start = Stopwatch.GetTimestamp();
                Consume(body());
                var elapsed = Stopwatch.GetTimestamp() - start;

                var nanoseconds = elapsed * (1_000_000_000.0 / Stopwatch.Frequency) / operations;

                if (nanoseconds < best)
                    best = nanoseconds;
            }

            return best;
        }

        private static void Consume(int value)
        {
            if (value == int.MinValue)
                throw new InvalidOperationException("unreachable; defeats dead-code elimination");
        }

        private record MatchingResult(int RestingOrders, double AddNsPerOp, double CancelNsPerOp,
            double MatchNsPerOp, double MixedNsPerOp, double MixedBytesPerOp);
    }
}
