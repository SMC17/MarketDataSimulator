using MarketData.Common.Books;
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
    /// Micro-benchmark of the four aggregated order book implementations across display depths.
    /// </summary>
    /// <remarks>
    /// Every implementation runs the identical pre-generated operation stream, so the comparison
    /// is of data structures rather than of workloads. Each configuration is timed over several
    /// trials. Setup is outside the timed region and the median is reported with min/max retained
    /// in JSON.
    /// </remarks>
    public static class BookBenchmark
    {
        private const int Operations = 200_000;
        private const int Trials = 7;
        private const int WarmupTrials = 3;

        public static int Run(string[] args)
        {
            var depths = new[] { 5, 10, 25, 50, 100, 250, 500, 1000 };
            var outputPath = (string)null;

            for (var i = 0; i < args.Length; i++)
            {
                if (args[i] == "--depths")
                    depths = args[++i].Split(',').Select(d => int.Parse(d, CultureInfo.InvariantCulture)).ToArray();
                else if (args[i] == "--out")
                    outputPath = args[++i];
            }

            Console.WriteLine($"Order book micro-benchmark: {Operations:N0} operations, median of {Trials} trials");
            Console.WriteLine($"Server GC: {System.Runtime.GCSettings.IsServerGC}, 64-bit: {Environment.Is64BitProcess}");
            Console.WriteLine();
            Console.WriteLine($"{"Depth",6} {"Implementation",18} {"Mixed ns/op",13} {"Touch ns/op",13} {"Top10 ns/op",13} {"Clear ns/op",13} {"Top10 B/op",11}");
            Console.WriteLine(new string('-', 94));

            var results = new List<BookBenchmarkResult>();

            // The whole sweep runs twice and only the second pass is recorded; see Sweep.
            Sweep(depths, null);
            Sweep(depths, results);

            if (outputPath is not null)
            {
                var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(outputPath));
                System.IO.Directory.CreateDirectory(directory);
                var report = new BookBenchmarkReport(DateTimeOffset.UtcNow,
                    RuntimeInformation.FrameworkDescription, RuntimeInformation.OSDescription,
                    RuntimeInformation.ProcessArchitecture.ToString(), Environment.ProcessorCount,
                    System.Runtime.GCSettings.IsServerGC, Operations, Trials, results);
                System.IO.File.WriteAllText(outputPath,
                    JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"Wrote {outputPath}");
            }

            return 0;
        }

        private static IOrderBook Create(string name, int depth, int band) => name switch
        {
            nameof(SortedArrayBook) => new SortedArrayBook(depth),
            nameof(VectorizedBook) => new VectorizedBook(depth),
            nameof(LadderBook) => new LadderBook(depth, -band, band),
            nameof(TreeBook) => new TreeBook(depth),
            _ => throw new ArgumentOutOfRangeException(nameof(name)),
        };

        /// <summary>
        /// A fixed operation stream, generated once from a fixed seed so every implementation and
        /// every trial sees byte-identical work.
        /// </summary>
        private static Operation[] GenerateStream(int band)
        {
            var random = new Random(20240817);
            var stream = new Operation[Operations];

            for (var i = 0; i < Operations; i++)
            {
                var side = random.Next(2) == 0 ? Side.Bid : Side.Ask;
                var price = random.Next(-band, band + 1);
                var roll = random.NextDouble();

                stream[i] = roll switch
                {
                    < 0.75 => new Operation(true, side, price, (uint)random.Next(1, 1000)),
                    _ => new Operation(false, side, price, 0),
                };
            }

            return stream;
        }

        private static Measurement MeasureMixed(string name, int depth, int band, Operation[] stream)
            => Measure(() =>
            {
                var book = Create(name, depth, band);
                return () =>
                {
                    var accumulator = 0;

                    for (var i = 0; i < stream.Length; i++)
                    {
                        var operation = stream[i];

                        if (operation.IsUpsert)
                            accumulator += book.Upsert(operation.Side, operation.Price, operation.Quantity) ? 1 : 0;
                        else
                            accumulator += book.Remove(operation.Side, operation.Price) ? 1 : 0;
                    }

                    return accumulator;
                };
            }, stream.Length);

        private static Measurement MeasureTouch(string name, int depth, int band, Operation[] stream)
        {
            var book = Create(name, depth, band);
            Populate(book, stream, depth);

            return Measure(() => () =>
            {
                var accumulator = 0;

                for (var i = 0; i < Operations; i++)
                {
                    if (book.TryGetBest((i & 1) == 0 ? Side.Bid : Side.Ask, out var level))
                        accumulator += level.Price;
                }

                return accumulator;
            }, Operations);
        }

        private static Measurement MeasureSnapshot(string name, int depth, int band, Operation[] stream)
        {
            var book = Create(name, depth, band);
            Populate(book, stream, depth);

            // Ten levels is what a depth-limited feed actually publishes, regardless of how many
            // the book retains internally - so this is the cost that shows up on the wire path.
            var buffer = new PriceLevel[10];
            const int iterations = Operations / 10;

            return Measure(() => () =>
            {
                var accumulator = 0;

                for (var i = 0; i < iterations; i++)
                    accumulator += book.CopyTo((i & 1) == 0 ? Side.Bid : Side.Ask, buffer);

                return accumulator;
            }, iterations);
        }

        /// <summary>
        /// Bytes allocated per top-of-book publish.
        /// </summary>
        /// <remarks>
        /// Allocation on a publish path is not a throughput detail, it is a latency hazard: bytes
        /// allocated per message become garbage collections, and a collection stalls the
        /// dissemination thread at an arbitrary moment. A publish path worth shipping allocates
        /// nothing, and this column is what proves whether it does.
        /// </remarks>
        private static double MeasureSnapshotAllocation(string name, int depth, int band, Operation[] stream)
        {
            var book = Create(name, depth, band);
            Populate(book, stream, depth);

            var buffer = new PriceLevel[10];
            const int iterations = 10_000;

            // Warm up first so one-off JIT and lazy-init allocations are not attributed per call.
            for (var i = 0; i < 1_000; i++)
                Consume(book.CopyTo(Side.Bid, buffer));

            var before = GC.GetAllocatedBytesForCurrentThread();

            for (var i = 0; i < iterations; i++)
                Consume(book.CopyTo((i & 1) == 0 ? Side.Bid : Side.Ask, buffer));

            return (GC.GetAllocatedBytesForCurrentThread() - before) / (double)iterations;
        }

        /// <summary>
        /// Cost of emptying a populated book, excluding the cost of populating it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Measured because replaying a session against published snapshots clears the book once
        /// per message, which turns an operation that looks like start-up housekeeping into one of
        /// the hottest on the path. An implementation whose clear is proportional to its price band
        /// rather than to the levels present is unusable there, and nothing else in this suite
        /// would reveal it.
        /// </para>
        /// <para>
        /// Clearing empties the book, so each measurement has to refill it first - and the refill
        /// is far more expensive than the clear it sets up. Timing the pair together, as this
        /// originally did, produces a column that is labelled "clear" and is in fact dominated by
        /// insertion: at depth 1000 the refill is two thousand upserts against a clear that touches
        /// a few hundred slots. So only the clear sits inside the timer, and the refill is charged
        /// to nobody.
        /// </para>
        /// <para>
        /// The timestamp pair costs on the order of tens of nanoseconds, which is why the loop
        /// accumulates many clears per trial rather than reporting a single one.
        /// </para>
        /// </remarks>
        private static Measurement MeasureClear(string name, int depth, int band, Operation[] stream)
        {
            var book = Create(name, depth, band);
            const int iterations = 2_000;

            var trials = new double[Trials];

            for (var trial = 0; trial < Trials + WarmupTrials; trial++)
            {
                long elapsed = 0;
                var accumulator = 0;

                for (var i = 0; i < iterations; i++)
                {
                    Populate(book, stream, depth);

                    var start = Stopwatch.GetTimestamp();
                    book.Clear();
                    elapsed += Stopwatch.GetTimestamp() - start;

                    accumulator += book.Count(Side.Bid);
                }

                Consume(accumulator);

                if (trial >= WarmupTrials)
                {
                    trials[trial - WarmupTrials] =
                        elapsed * (1_000_000_000.0 / Stopwatch.Frequency) / iterations;
                }
            }

            Array.Sort(trials);
            return new Measurement(Math.Round(trials[Trials / 2], 2),
                Math.Round(trials[0], 2), Math.Round(trials[^1], 2));
        }

        /// <summary>
        /// Runs the sweep, recording into <paramref name="results"/> when it is not null.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Called twice, and the first pass is thrown away. That is not superstition; it is the
        /// only thing that made the numbers stable, and what it corrects is worth stating because
        /// it silently invalidated an earlier revision of the published table.
        /// </para>
        /// <para>
        /// Whichever depth was measured <em>first</em> reported wrong figures for every
        /// implementation except the one measured first within it - and the distortion followed
        /// the position in the sweep, not the depth. Reversing the depth order moved it from depth
        /// 10 to depth 1000. At depth 10 it was large enough to reverse the ranking, and depth 10
        /// is the depth this feed actually ships.
        /// </para>
        /// <para>
        /// Two mechanisms are behind it, both artefacts of a managed runtime rather than of the
        /// data structures. Every measurement calls through <see cref="IOrderBook"/> from one
        /// shared call site, so the first implementation to reach it makes it monomorphic and the
        /// JIT devirtualizes for that type, leaving the others to fail a type guard on every call.
        /// And promotion to optimised code is not merely call-count driven - the compilation
        /// happens on a background thread, so a configuration can finish measuring while still
        /// executing unoptimised code. Per-measurement warm-up trials cannot fix the second one,
        /// because they do not buy wall-clock time for a compilation to land.
        /// </para>
        /// <para>
        /// A discarded full pass fixes both: by the time anything is recorded, every call site has
        /// seen every implementation and every body has long since been promoted.
        /// </para>
        /// </remarks>
        private static void Sweep(int[] depths, List<BookBenchmarkResult> results)
        {
            foreach (var depth in depths)
            {
                // The price band is sized to several times the depth so the book stays full,
                // evictions happen constantly, and the depth cap is genuinely exercised.
                var band = Math.Max(64, depth * 4);
                var stream = GenerateStream(band);

                foreach (var name in Implementations)
                {
                    var mixed = MeasureMixed(name, depth, band, stream);
                    var touch = MeasureTouch(name, depth, band, stream);
                    var snapshot = MeasureSnapshot(name, depth, band, stream);
                    var snapshotBytes = MeasureSnapshotAllocation(name, depth, band, stream);
                    var clear = MeasureClear(name, depth, band, stream);

                    if (results is null)
                        continue;

                    Console.WriteLine($"{depth,6} {name,18} {mixed.Median,13:F1} {touch.Median,13:F1} " +
                        $"{snapshot.Median,13:F1} {clear.Median,13:F1} {snapshotBytes,11:F1}");
                    results.Add(new BookBenchmarkResult(depth, name,
                        mixed, touch, snapshot, clear, Math.Round(snapshotBytes, 1)));
                }

                if (results is not null)
                    Console.WriteLine();
            }
        }

        private static readonly string[] Implementations =
        {
            nameof(SortedArrayBook), nameof(VectorizedBook), nameof(LadderBook), nameof(TreeBook),
        };

        private static void Populate(IOrderBook book, Operation[] stream, int depth)
        {
            foreach (var operation in stream)
            {
                if (operation.IsUpsert)
                    book.Upsert(operation.Side, operation.Price, operation.Quantity);

                if (book.Count(Side.Bid) >= depth && book.Count(Side.Ask) >= depth)
                    break;
            }
        }

        /// <summary>
        /// Prepares outside the timed region and records nanoseconds per operation.
        /// </summary>
        private static Measurement Measure(Func<Func<int>> prepare, int operations)
        {
            for (var i = 0; i < WarmupTrials; i++)
                Consume(prepare()());

            var samples = new double[Trials];

            for (var trial = 0; trial < Trials; trial++)
            {
                var body = prepare();
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

        private readonly record struct Operation(bool IsUpsert, Side Side, int Price, uint Quantity);

        private sealed record Measurement(double Median, double Min, double Max);
        private sealed record BookBenchmarkResult(int Depth, string Implementation,
            Measurement MixedNsPerOp, Measurement TouchNsPerOp, Measurement SnapshotNsPerOp,
            Measurement ClearNsPerOp, double SnapshotBytesPerOp);
        private sealed record BookBenchmarkReport(DateTimeOffset TimestampUtc, string Runtime,
            string OperatingSystem, string Architecture, int LogicalProcessors, bool ServerGc,
            int Operations, int Trials, List<BookBenchmarkResult> Results);
    }
}
