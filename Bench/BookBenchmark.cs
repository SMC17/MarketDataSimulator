using MarketData.Common.Books;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace MarketData.Bench
{
    /// <summary>
    /// Micro-benchmark of the three order book implementations across display depths.
    /// </summary>
    /// <remarks>
    /// Every implementation runs the identical pre-generated operation stream, so the comparison
    /// is of data structures rather than of workloads. Each configuration is timed over several
    /// trials and reported by its minimum: the fastest observed run is the one least perturbed by
    /// scheduling, interrupts and background work, and for a CPU-bound micro-benchmark that is a
    /// better estimate of true cost than an average over noise.
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

            Console.WriteLine($"Order book micro-benchmark: {Operations:N0} operations, {Trials} trials, minimum reported");
            Console.WriteLine($"Server GC: {System.Runtime.GCSettings.IsServerGC}, 64-bit: {Environment.Is64BitProcess}");
            Console.WriteLine();
            Console.WriteLine($"{"Depth",6} {"Implementation",18} {"Mixed ns/op",13} {"Touch ns/op",13} {"Top10 ns/op",13} {"Top10 B/op",11}");
            Console.WriteLine(new string('-', 80));

            var results = new List<BookBenchmarkResult>();

            foreach (var depth in depths)
            {
                // The price band is sized to several times the depth so the book stays full,
                // evictions happen constantly, and the depth cap is genuinely exercised.
                var band = Math.Max(64, depth * 4);
                var stream = GenerateStream(band);

                foreach (var name in new[] { nameof(SortedArrayBook), nameof(LadderBook), nameof(TreeBook) })
                {
                    var mixed = MeasureMixed(name, depth, band, stream);
                    var touch = MeasureTouch(name, depth, band, stream);
                    var snapshot = MeasureSnapshot(name, depth, band, stream);
                    var snapshotBytes = MeasureSnapshotAllocation(name, depth, band, stream);

                    Console.WriteLine($"{depth,6} {name,18} {mixed,13:F1} {touch,13:F1} {snapshot,13:F1} {snapshotBytes,11:F1}");
                    results.Add(new BookBenchmarkResult(depth, name,
                        Math.Round(mixed, 2), Math.Round(touch, 2), Math.Round(snapshot, 2),
                        Math.Round(snapshotBytes, 1)));
                }

                Console.WriteLine();
            }

            if (outputPath is not null)
            {
                var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(outputPath));
                System.IO.Directory.CreateDirectory(directory);
                System.IO.File.WriteAllText(outputPath,
                    JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"Wrote {outputPath}");
            }

            return 0;
        }

        private static IOrderBook Create(string name, int depth, int band) => name switch
        {
            nameof(SortedArrayBook) => new SortedArrayBook(depth),
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

        private static double MeasureMixed(string name, int depth, int band, Operation[] stream)
            => Measure(() =>
            {
                var book = Create(name, depth, band);
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
            }, stream.Length);

        private static double MeasureTouch(string name, int depth, int band, Operation[] stream)
        {
            var book = Create(name, depth, band);
            Populate(book, stream, depth);

            return Measure(() =>
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

        private static double MeasureSnapshot(string name, int depth, int band, Operation[] stream)
        {
            var book = Create(name, depth, band);
            Populate(book, stream, depth);

            // Ten levels is what a depth-limited feed actually publishes, regardless of how many
            // the book retains internally - so this is the cost that shows up on the wire path.
            var buffer = new PriceLevel[10];
            const int iterations = Operations / 10;

            return Measure(() =>
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
        /// Times <paramref name="body"/> over several trials and returns the minimum nanoseconds
        /// per operation. The returned accumulator is fed to <see cref="Consume"/> so the JIT
        /// cannot delete the work being measured.
        /// </summary>
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

        private readonly record struct Operation(bool IsUpsert, Side Side, int Price, uint Quantity);

        private record BookBenchmarkResult(int Depth, string Implementation,
            double MixedNsPerOp, double TouchNsPerOp, double SnapshotNsPerOp, double SnapshotBytesPerOp);
    }
}
