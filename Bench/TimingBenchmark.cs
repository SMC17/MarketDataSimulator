using MarketData.Common.Time;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MarketData.Bench
{
    /// <summary>
    /// What this host's clock can actually resolve, and whether pinning threads helps here.
    /// </summary>
    /// <remarks>
    /// Both halves exist to keep an optimisation honest. Timestamping and CPU pinning are standard
    /// advice for latency-sensitive systems, and standard advice is exactly the kind of thing that
    /// gets adopted without measurement. If pinning does not help on this host, saying so is worth
    /// more than adopting it anyway.
    /// </remarks>
    public static class TimingBenchmark
    {
        public static int Run(string[] args)
        {
            var trials = 7;
            var iterations = 2_000_000;
            string outputPath = null;

            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--trials": trials = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--iterations": iterations = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--out": outputPath = args[++i]; break;
                }
            }

            var results = new List<object>();

            // ------------------------------------------------------------------ clock
            Console.WriteLine("Clock");
            Console.WriteLine(new string('-', 72));

            var resolution = UncertainClock.MeasureResolutionNanoseconds();
            var readCost = MeasureReadCost(iterations, trials);

            Console.WriteLine($"  Stopwatch frequency        : {Stopwatch.Frequency:N0} Hz");
            Console.WriteLine($"  High resolution            : {Stopwatch.IsHighResolution}");
            Console.WriteLine($"  Smallest observable step   : {resolution:N0} ns");
            Console.WriteLine($"  Cost of one reading        : {readCost:N1} ns");
            Console.WriteLine();
            Console.WriteLine("  The smallest observable step is a floor on this clock's error, not the");
            Console.WriteLine("  whole of it. Agreement with another host is a separate question this");
            Console.WriteLine("  machine cannot answer: there is no PTP grandmaster and no NIC hardware");
            Console.WriteLine("  timestamping available here, so cross-host uncertainty is unmeasured");
            Console.WriteLine("  rather than small.");
            Console.WriteLine();

            results.Add(new
            {
                Kind = "clock",
                StopwatchFrequency = Stopwatch.Frequency,
                Stopwatch.IsHighResolution,
                ResolutionNanoseconds = resolution,
                ReadCostNanoseconds = Math.Round(readCost, 1),
                Source = TimestampSource.SoftwareMonotonic.ToString(),
                PtpAvailable = false,
                HardwareTimestampsAvailable = false,
            });

            // ------------------------------------------------------------------ placement
            var capabilities = ProcessorPlacement.Detect();

            Console.WriteLine("Placement");
            Console.WriteLine(new string('-', 72));
            Console.WriteLine($"  Logical processors         : {capabilities.LogicalProcessors}");
            Console.WriteLine($"  Allowed to this process    : {capabilities.AllowedProcessors.Count} " +
                              $"[{string.Join(",", capabilities.AllowedProcessors)}]");
            Console.WriteLine($"  NUMA nodes                 : {capabilities.NumaNodes}");
            Console.WriteLine($"  Can pin threads            : {capabilities.CanPinThreads}");
            Console.WriteLine($"  Notes                      : {capabilities.Notes}");
            Console.WriteLine();

            var migrations = ProcessorPlacement.CountMigrations(TimeSpan.FromSeconds(2));
            Console.WriteLine($"  Migrations while unpinned  : {migrations} in 2 s");

            var unpinned = MeasureWorkNanoseconds(iterations / 4, trials, pinTo: -1);
            var pinnedTo = capabilities.AllowedProcessors.Count > 0 ? capabilities.AllowedProcessors[0] : -1;
            var pinned = MeasureWorkNanoseconds(iterations / 4, trials, pinTo: pinnedTo);

            Console.WriteLine($"  Hot loop, unpinned         : {unpinned:N2} ns/op");
            Console.WriteLine($"  Hot loop, pinned to cpu {pinnedTo,-2} : {pinned:N2} ns/op");

            var change = (pinned - unpinned) / unpinned * 100;
            Console.WriteLine($"  Change from pinning        : {change:+0.0;-0.0;0.0} %");
            Console.WriteLine();

            // The verdict, stated rather than left to the reader to infer.
            var verdict = !capabilities.PinningCouldMatter
                ? "not justified: this process has too little placement freedom for pinning to mean anything"
                : migrations == 0
                    ? "not justified: the unpinned thread never migrated, so pinning removes a cost that was not being paid"
                    : Math.Abs(change) < 2
                        ? "not justified: the difference is within run-to-run noise on this host"
                        : change < 0
                            ? $"justified here: pinning was {Math.Abs(change):N1}% faster, and migrations were observed"
                            : $"harmful here: pinning was {change:N1}% slower, likely by removing the scheduler's freedom to avoid a busy core";

            Console.WriteLine($"  VERDICT: {verdict}");
            Console.WriteLine();
            Console.WriteLine("  Kernel bypass (AF_XDP, io_uring, a userspace stack) is not evaluated:");
            Console.WriteLine("  it needs privileges and NIC support this environment does not provide.");
            Console.WriteLine("  It stays unmeasured, and therefore unclaimed.");

            results.Add(new
            {
                Kind = "placement",
                capabilities.LogicalProcessors,
                AllowedProcessors = capabilities.AllowedProcessors.Count,
                capabilities.NumaNodes,
                capabilities.CanPinThreads,
                MigrationsWhileUnpinnedIn2s = migrations,
                UnpinnedNanosecondsPerOp = Math.Round(unpinned, 2),
                PinnedNanosecondsPerOp = Math.Round(pinned, 2),
                PercentChangeFromPinning = Math.Round(change, 1),
                Verdict = verdict,
                KernelBypassEvaluated = false,
            });

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

        private static double MeasureReadCost(int iterations, int trials)
        {
            var best = double.MaxValue;

            for (var trial = 0; trial < trials + 1; trial++)
            {
                var start = Stopwatch.GetTimestamp();
                long accumulator = 0;

                for (var i = 0; i < iterations; i++)
                    accumulator += Stopwatch.GetTimestamp();

                var elapsed = Stopwatch.GetTimestamp() - start;

                if (accumulator == long.MinValue)
                    throw new InvalidOperationException("unreachable; defeats dead-code elimination");

                if (trial == 0)
                    continue;

                var nanoseconds = elapsed * (1_000_000_000.0 / Stopwatch.Frequency) / iterations;

                if (nanoseconds < best)
                    best = nanoseconds;
            }

            return best;
        }

        /// <summary>
        /// A cache-sensitive hot loop, run on a dedicated thread, optionally pinned.
        /// </summary>
        /// <remarks>
        /// Deliberately touches enough memory to care about which core it is on. A loop that fits
        /// entirely in registers would be insensitive to placement and would report "pinning does
        /// nothing" no matter what the truth was.
        /// </remarks>
        private static double MeasureWorkNanoseconds(int iterations, int trials, int pinTo)
        {
            var best = double.MaxValue;
            var data = new int[1 << 16];

            for (var i = 0; i < data.Length; i++)
                data[i] = i;

            for (var trial = 0; trial < trials + 1; trial++)
            {
                double elapsedNanoseconds = 0;

                var thread = new Thread(() =>
                {
                    if (pinTo >= 0)
                        ProcessorPlacement.TryPinCurrentThread(pinTo);

                    var start = Stopwatch.GetTimestamp();
                    var sum = 0;
                    var mask = data.Length - 1;

                    for (var i = 0; i < iterations; i++)
                        sum += data[(i * 7919) & mask];

                    var ticks = Stopwatch.GetTimestamp() - start;

                    if (sum == int.MinValue)
                        throw new InvalidOperationException("unreachable");

                    elapsedNanoseconds = ticks * (1_000_000_000.0 / Stopwatch.Frequency) / iterations;
                })
                { IsBackground = true };

                thread.Start();
                thread.Join();

                if (trial == 0)
                    continue;

                if (elapsedNanoseconds < best)
                    best = elapsedNanoseconds;
            }

            return best;
        }
    }
}
