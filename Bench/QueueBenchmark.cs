using MarketData.Common.Concurrency;
using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace MarketData.Bench
{
    /// <summary>
    /// The lock-free ring against the channel it would replace, on the dissemination hand-off.
    /// </summary>
    /// <remarks>
    /// Both are measured the same way: one producer thread, one consumer thread, a fixed number of
    /// items, spin-waiting rather than blocking so the measurement is of the queue and not of the
    /// operating system's scheduler. The single-threaded rows isolate raw per-operation cost from
    /// the cost of the hand-off between cores.
    /// </remarks>
    public static class QueueBenchmark
    {
        public static int Run(string[] args)
        {
            var items = 5_000_000;
            var capacity = 8192;
            var trials = 3;
            string outputPath = null;

            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--items": items = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--capacity": capacity = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--trials": trials = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--out": outputPath = args[++i]; break;
                }
            }

            if (items <= 0 || capacity <= 0 || trials < 3)
                throw new ArgumentOutOfRangeException(nameof(args));

            Console.WriteLine($"Queue hand-off: {items:N0} items, capacity {capacity:N0}, median of {trials}");
            Console.WriteLine($"Cores: {Environment.ProcessorCount}, server GC: {System.Runtime.GCSettings.IsServerGC}");
            Console.WriteLine();
            Console.WriteLine($"{"Queue",34} {"median ns",10} {"min ns",9} {"max ns",9} {"M items/s",12} {"B/item",9}");
            Console.WriteLine(new string('-', 91));

            var results = new[]
            {
                Measure("RingBuffer (single thread)", trials, () => RingSingleThreaded(items, capacity)),
                Measure("Channel (single thread)", trials, () => ChannelSingleThreaded(items, capacity)),
                Measure("RingBuffer (producer + consumer)", trials, () => RingConcurrent(items, capacity)),
                Measure("RingBuffer batched (prod + cons)", trials, () => RingConcurrentBatched(items, capacity)),
                Measure("Channel (producer + consumer)", trials, () => ChannelConcurrent(items, capacity)),
            };

            foreach (var result in results)
            {
                Console.WriteLine($"{result.Name,34} {result.MedianNanosecondsPerItem,10:F1} " +
                                  $"{result.MinNanosecondsPerItem,9:F1} {result.MaxNanosecondsPerItem,9:F1} " +
                                  $"{items / result.MedianSeconds / 1_000_000,12:F2} {result.BytesPerItem,9:F3}");
            }

            Console.WriteLine();

            var ring = Array.Find(results, r => r.Name.StartsWith("RingBuffer (producer"));
            var channel = Array.Find(results, r => r.Name.StartsWith("Channel (producer"));
            Console.WriteLine($"Concurrent hand-off: ring is " +
                $"{channel.MedianNanosecondsPerItem / ring.MedianNanosecondsPerItem:F2}x the channel's throughput.");

            if (outputPath is not null)
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(outputPath)));
                var report = new QueueReport(DateTimeOffset.UtcNow,
                    RuntimeInformation.FrameworkDescription, RuntimeInformation.OSDescription,
                    RuntimeInformation.ProcessArchitecture.ToString(), Environment.ProcessorCount,
                    System.Runtime.GCSettings.IsServerGC, items, capacity, trials, results);
                System.IO.File.WriteAllText(outputPath, JsonSerializer.Serialize(report,
                    new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"Wrote {outputPath}");
            }

            return 0;
        }

        private static Result Measure(string name, int trials, Func<long> body)
        {
            body(); // warm up JIT and let the thread pool settle

            var seconds = new double[trials];
            var nanoseconds = new double[trials];
            var bytes = new double[trials];

            for (var trial = 0; trial < trials; trial++)
            {
                var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
                var started = Stopwatch.GetTimestamp();
                var count = body();
                var elapsed = (Stopwatch.GetTimestamp() - started) / (double)Stopwatch.Frequency;
                var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;

                seconds[trial] = elapsed;
                nanoseconds[trial] = count == 0 ? 0 : elapsed * 1e9 / count;
                bytes[trial] = count == 0 ? 0 : allocated / (double)count;
            }

            Array.Sort(seconds);
            Array.Sort(nanoseconds);
            Array.Sort(bytes);
            var middle = trials / 2;

            return new Result(name,
                Math.Round(seconds[middle], 6),
                Math.Round(nanoseconds[middle], 2),
                Math.Round(nanoseconds[0], 2),
                Math.Round(nanoseconds[^1], 2),
                Math.Round(bytes[middle], 4));
        }

        private static long RingSingleThreaded(int items, int capacity)
        {
            var ring = new RingBuffer<long>(capacity);

            for (long i = 0; i < items; i++)
            {
                ring.TryWrite(i);
                ring.TryRead(out _);
            }

            return items;
        }

        private static long ChannelSingleThreaded(int items, int capacity)
        {
            var channel = Channel.CreateBounded<long>(new BoundedChannelOptions(capacity)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait,
            });

            for (long i = 0; i < items; i++)
            {
                channel.Writer.TryWrite(i);
                channel.Reader.TryRead(out _);
            }

            return items;
        }

        private static long RingConcurrent(int items, int capacity)
        {
            var ring = new RingBuffer<long>(capacity);

            var consumer = Task.Factory.StartNew(() =>
            {
                for (var read = 0; read < items;)
                {
                    if (ring.TryRead(out _))
                        read++;
                    else
                        Thread.SpinWait(1);
                }
            }, TaskCreationOptions.LongRunning);

            for (long i = 0; i < items; i++)
            {
                while (!ring.TryWrite(i))
                    Thread.SpinWait(1);
            }

            consumer.Wait();
            return items;
        }

        private static long RingConcurrentBatched(int items, int capacity)
        {
            var ring = new RingBuffer<long>(capacity);

            var consumer = Task.Factory.StartNew(() =>
            {
                for (var read = 0; read < items;)
                {
                    var count = ring.PeekBatch().Length;

                    if (count == 0)
                    {
                        Thread.SpinWait(1);
                        continue;
                    }

                    // One release store for the whole run instead of one per item.
                    ring.Release(count);
                    read += count;
                }
            }, TaskCreationOptions.LongRunning);

            for (long i = 0; i < items; i++)
            {
                while (!ring.TryWrite(i))
                    Thread.SpinWait(1);
            }

            consumer.Wait();
            return items;
        }

        private static long ChannelConcurrent(int items, int capacity)
        {
            var channel = Channel.CreateBounded<long>(new BoundedChannelOptions(capacity)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait,
            });

            var consumer = Task.Factory.StartNew(() =>
            {
                for (var read = 0; read < items;)
                {
                    if (channel.Reader.TryRead(out _))
                        read++;
                    else
                        Thread.SpinWait(1);
                }
            }, TaskCreationOptions.LongRunning);

            for (long i = 0; i < items; i++)
            {
                while (!channel.Writer.TryWrite(i))
                    Thread.SpinWait(1);
            }

            consumer.Wait();
            return items;
        }

        private record Result(string Name, double MedianSeconds, double MedianNanosecondsPerItem,
            double MinNanosecondsPerItem, double MaxNanosecondsPerItem, double BytesPerItem);

        private sealed record QueueReport(DateTimeOffset TimestampUtc, string Runtime,
            string OperatingSystem, string Architecture, int LogicalProcessors, bool ServerGc,
            int Items, int Capacity, int Trials, Result[] Results);
    }
}
