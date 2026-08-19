using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace MarketData.Common.Time
{
    /// <summary>What this host will actually let the process do about placement.</summary>
    public sealed record PlacementCapabilities(
        bool CanPinThreads,
        bool HasNumaTopology,
        int NumaNodes,
        int LogicalProcessors,
        IReadOnlyList<int> AllowedProcessors,
        string Notes)
    {
        /// <summary>Whether pinning could plausibly help: more than one node, more than one CPU.</summary>
        public bool PinningCouldMatter => CanPinThreads && AllowedProcessors.Count > 1;
    }

    /// <summary>
    /// CPU affinity and NUMA placement, reported honestly and applied only where possible.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Pinning a thread to a core is standard advice for latency-sensitive work, and the reasoning
    /// is sound: a migrated thread arrives on a core whose caches know nothing about it, and on a
    /// multi-socket machine it may arrive with its memory attached to the wrong node entirely.
    /// </para>
    /// <para>
    /// It is still advice, not a result. Whether it helps <em>here</em> depends on how many cores
    /// the cgroup allows, whether the host has more than one NUMA node, and whether anything else
    /// is competing. Those are measurable, so this type reports them, and the benchmark measures
    /// the effect rather than assuming it. On a shared virtual host with one NUMA node, pinning
    /// can easily make things worse by removing the scheduler's freedom to avoid a busy core.
    /// </para>
    /// </remarks>
    public static class ProcessorPlacement
    {
        /// <summary>Reports what placement control is available, without changing anything.</summary>
        public static PlacementCapabilities Detect()
        {
            var notes = new List<string>();
            var logical = Environment.ProcessorCount;
            var allowed = AllowedProcessors();

            var numaNodes = CountNumaNodes(notes);
            var canPin = OperatingSystem.IsLinux() || OperatingSystem.IsWindows();

            if (!canPin)
                notes.Add("thread affinity is not supported on this platform");

            if (allowed.Count < logical)
                notes.Add($"cgroup restricts this process to {allowed.Count} of {logical} processors");

            if (numaNodes <= 1)
                notes.Add("single NUMA node: cross-node memory placement cannot be a factor here");

            return new PlacementCapabilities(
                canPin, numaNodes > 1, numaNodes, logical, allowed,
                notes.Count == 0 ? "no restrictions detected" : string.Join("; ", notes));
        }

        /// <summary>Processors this process is actually permitted to run on.</summary>
        /// <remarks>
        /// Read from the process affinity mask rather than assumed to be
        /// <see cref="Environment.ProcessorCount"/>. In a container those differ routinely, and
        /// pinning to a processor outside the mask fails or is silently ignored.
        /// </remarks>
        public static IReadOnlyList<int> AllowedProcessors()
        {
            var allowed = new List<int>();

            try
            {
                var mask = (long)Process.GetCurrentProcess().ProcessorAffinity;

                for (var i = 0; i < 64; i++)
                {
                    if ((mask & (1L << i)) != 0)
                        allowed.Add(i);
                }
            }
            catch (PlatformNotSupportedException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (InvalidOperationException)
            {
            }

            if (allowed.Count == 0)
                allowed.AddRange(Enumerable.Range(0, Environment.ProcessorCount));

            return allowed;
        }

        private static int CountNumaNodes(List<string> notes)
        {
            if (!OperatingSystem.IsLinux())
                return 1;

            try
            {
                const string path = "/sys/devices/system/node";

                if (!Directory.Exists(path))
                {
                    notes.Add("no /sys NUMA topology exposed");
                    return 1;
                }

                var nodes = Directory.GetDirectories(path, "node*")
                    .Count(directory => int.TryParse(
                        Path.GetFileName(directory).AsSpan("node".Length), out _));

                return Math.Max(1, nodes);
            }
            catch (IOException)
            {
                return 1;
            }
            catch (UnauthorizedAccessException)
            {
                return 1;
            }
        }

        /// <summary>
        /// Pins the calling thread to one processor.
        /// </summary>
        /// <returns>True if the pin took effect.</returns>
        /// <remarks>
        /// Returns a bool rather than throwing, because failing to pin is a normal outcome in a
        /// container and is not an error - the caller carries on unpinned. What would be an error
        /// is believing a pin succeeded when it did not, which is why the result is not ignorable.
        /// </remarks>
        public static bool TryPinCurrentThread(int processorId)
        {
            if (!OperatingSystem.IsLinux())
                return false;

            if (!AllowedProcessors().Contains(processorId))
                return false;

            try
            {
                // One 64-bit word covers the processor counts this program can encounter; a host
                // with more than 64 CPUs would need the full cpu_set_t.
                var mask = 1UL << processorId;
                return SchedSetAffinity(0, sizeof(ulong), ref mask) == 0;
            }
            catch (DllNotFoundException)
            {
                return false;
            }
            catch (EntryPointNotFoundException)
            {
                return false;
            }
        }

        [DllImport("libc", EntryPoint = "sched_setaffinity", SetLastError = true)]
        private static extern int SchedSetAffinity(int pid, nint cpuSetSize, ref ulong mask);

        [DllImport("libc", EntryPoint = "sched_getcpu")]
        private static extern int SchedGetCpu();

        /// <summary>The processor the calling thread is running on right now, or -1.</summary>
        public static int CurrentProcessor()
        {
            if (!OperatingSystem.IsLinux())
                return -1;

            try
            {
                return SchedGetCpu();
            }
            catch (DllNotFoundException)
            {
                return -1;
            }
            catch (EntryPointNotFoundException)
            {
                return -1;
            }
        }

        /// <summary>
        /// Counts how often a thread migrates between processors over a busy interval.
        /// </summary>
        /// <remarks>
        /// The measurement that decides whether pinning is worth anything on this host. If an
        /// unpinned thread never migrates, pinning it cannot help, and any improvement attributed
        /// to pinning came from somewhere else.
        /// </remarks>
        public static int CountMigrations(TimeSpan duration, CancellationToken token = default)
        {
            var deadline = Stopwatch.StartNew();
            var migrations = 0;
            var last = CurrentProcessor();

            if (last < 0)
                return -1;

            while (deadline.Elapsed < duration && !token.IsCancellationRequested)
            {
                // Busy work, so the scheduler has a reason to move the thread.
                Thread.SpinWait(1_000);

                var current = CurrentProcessor();

                if (current >= 0 && current != last)
                {
                    migrations++;
                    last = current;
                }
            }

            return migrations;
        }
    }
}
