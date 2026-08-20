using MarketData.Common.Books;
using MarketData.Common.Risk;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace MarketData.Bench
{
    /// <summary>
    /// What the pre-trade gate costs per order, and whether it survives contention.
    /// </summary>
    /// <remarks>
    /// The gate sits on the order path, so its cost is paid once per message and its scaling is
    /// paid once per participant. Both are measured rather than asserted: a risk check that is
    /// "obviously cheap" is how a venue discovers its throughput ceiling is the risk layer.
    /// </remarks>
    public static class RiskBenchmark
    {
        public static int Run(string[] args)
        {
            var iterations = 2_000_000;
            var trials = 5;
            string outputPath = null;

            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--iterations": iterations = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--trials": trials = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--out": outputPath = args[++i]; break;
                }
            }

            Console.WriteLine($"Pre-trade risk gate: {iterations:N0} checks, median of {trials} trials");
            Console.WriteLine();

            var results = new List<object>();

            Console.WriteLine($"{"Case",34} {"ns/check",12} {"checks/s",16} {"B/check",10}");
            Console.WriteLine(new string('-', 78));

            foreach (var (name, build, order) in Cases())
            {
                var gate = build();
                var samples = new List<double>();
                long bytes = 0;

                for (var trial = 0; trial < trials + 1; trial++)
                {
                    // Warm, then measure. The first trial pays for JIT on everyone's behalf.
                    var before = GC.GetAllocatedBytesForCurrentThread();
                    var stopwatch = Stopwatch.StartNew();

                    for (var i = 0; i < iterations; i++)
                        Consume(gate.Check(order));

                    stopwatch.Stop();
                    var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

                    if (trial == 0)
                        continue;

                    samples.Add(stopwatch.Elapsed.TotalNanoseconds / iterations);
                    bytes = allocated / iterations;
                }

                var median = Median(samples);
                var perSecond = 1_000_000_000.0 / median;

                Console.WriteLine($"{name,34} {median,12:N1} {perSecond,16:N0} {bytes,10}");

                results.Add(new
                {
                    Kind = "gate",
                    Case = name,
                    NanosecondsPerCheck = Math.Round(median, 1),
                    ChecksPerSecond = Math.Round(perSecond, 0),
                    BytesPerCheck = bytes,
                });
            }

            // ------------------------------------------------------------------ contention
            Console.WriteLine();
            Console.WriteLine("Contention: many participants sharing one gate");
            Console.WriteLine();
            Console.WriteLine($"{"Threads",10} {"ns/check",12} {"checks/s",16} {"scaling",10}");
            Console.WriteLine(new string('-', 52));

            double singleThreaded = 0;

            foreach (var threads in new[] { 1, 2, 4 })
            {
                var gate = new PreTradeRiskGate();

                for (var t = 0; t < threads; t++)
                {
                    gate.Register($"P{t}", new ParticipantLimits(
                        MaxOrderQuantity: uint.MaxValue, MaxOrderNotional: long.MaxValue,
                        MaxNetPosition: long.MaxValue, CreditLimit: long.MaxValue,
                        MaxMessagesPerSecond: int.MaxValue, CollarBasisPoints: 0));
                    gate.GrantAll($"P{t}", Entitlement.All);
                }

                var perThread = iterations / threads / 4;
                var best = double.MaxValue;

                for (var trial = 0; trial < trials + 1; trial++)
                {
                    var stopwatch = Stopwatch.StartNew();

                    Parallel.For(0, threads, t =>
                    {
                        var order = new OrderRequest($"P{t}", 1, Side.Bid, 100, 10);

                        for (var i = 0; i < perThread; i++)
                            Consume(gate.Check(order));
                    });

                    stopwatch.Stop();

                    if (trial == 0)
                        continue;

                    var nanoseconds = stopwatch.Elapsed.TotalNanoseconds / (perThread * threads);

                    if (nanoseconds < best)
                        best = nanoseconds;
                }

                if (threads == 1)
                    singleThreaded = best;

                var throughput = threads * 1_000_000_000.0 / best / threads;
                var scaling = singleThreaded / best;

                Console.WriteLine($"{threads,10} {best,12:N1} {1_000_000_000.0 / best,16:N0} {scaling,9:N2}x");

                results.Add(new
                {
                    Kind = "contention",
                    Threads = threads,
                    NanosecondsPerCheck = Math.Round(best, 1),
                    ChecksPerSecond = Math.Round(1_000_000_000.0 / best, 0),
                    ScalingVersusSingleThread = Math.Round(scaling, 2),
                });
            }

            Console.WriteLine();
            Console.WriteLine("Each participant owns its own credit and rate state, so threads contend only on");
            Console.WriteLine("the shared entitlement and participant maps, which are read-mostly. That is the");
            Console.WriteLine("design intent; the measurement above is what it actually buys, and it does not");
            Console.WriteLine("scale with cores - throughput peaks around two threads and at four is back at");
            Console.WriteLine("or below the single-threaded figure, varying by tens of percent between runs.");
            Console.WriteLine("On a four-core host shared with the load harness that is unsurprising, but");
            Console.WriteLine("it means the gate should be read as roughly ten million checks per second in");
            Console.WriteLine("total rather than per core, and a venue needing more would shard participants");
            Console.WriteLine("across gates rather than expecting this one to scale with cores.");

            if (outputPath is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath)));
                File.WriteAllText(outputPath,
                    JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine();
                Console.WriteLine($"Wrote {outputPath}");
            }

            return 0;
        }

        private static IEnumerable<(string Name, Func<PreTradeRiskGate> Build, OrderRequest Order)> Cases()
        {
            static PreTradeRiskGate Permissive()
            {
                var gate = new PreTradeRiskGate();
                gate.Register("P", new ParticipantLimits(
                    MaxOrderQuantity: uint.MaxValue, MaxOrderNotional: long.MaxValue,
                    MaxNetPosition: long.MaxValue, CreditLimit: long.MaxValue,
                    MaxMessagesPerSecond: int.MaxValue, CollarBasisPoints: 0));
                gate.GrantAll("P", Entitlement.All);
                return gate;
            }

            yield return ("accept, no collar", Permissive,
                new OrderRequest("P", 1, Side.Bid, 100, 10));

            yield return ("accept, with price collar", () =>
            {
                var gate = Permissive();
                gate.SetReferencePrice(1, 100);
                return gate;
            }, new OrderRequest("P", 1, Side.Bid, 100, 10));

            yield return ("reject, unknown participant", () => new PreTradeRiskGate(),
                new OrderRequest("NOBODY", 1, Side.Bid, 100, 10));

            yield return ("reject, oversized", () =>
            {
                var gate = new PreTradeRiskGate();
                gate.Register("P", new ParticipantLimits(MaxOrderQuantity: 1));
                gate.GrantAll("P", Entitlement.All);
                return gate;
            }, new OrderRequest("P", 1, Side.Bid, 100, 1_000));

            yield return ("reject, global kill engaged", () =>
            {
                var gate = Permissive();
                gate.EngageGlobalKill();
                return gate;
            }, new OrderRequest("P", 1, Side.Bid, 100, 10));
        }

        private static void Consume(RiskDecision decision)
        {
            if (decision.Reason == (RiskRejectReason)byte.MaxValue)
                throw new InvalidOperationException("unreachable; defeats dead-code elimination");
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
