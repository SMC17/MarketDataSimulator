using MarketData.Common.Concurrency;
using System;
using System.Diagnostics;
using System.Globalization;
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

            Console.WriteLine($"Queue hand-off: {items:N0} items, capacity {capacity:N0}, best of {trials}");
            Console.WriteLine($"Cores: {Environment.ProcessorCount}, server GC: {System.Runtime.GCSettings.IsServerGC}");
            Console.WriteLine();
            Console.WriteLine($"{"Queue",34} {"ns/item",10} {"M items/s",12} {"B/item",9}");
            Console.WriteLine(new string('-', 70));

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
                Console.WriteLine($"{result.Name,34} {result.NanosecondsPerItem,10:F1} " +
                                  $"{items / result.Seconds / 1_000_000,12:F2} {result.BytesPerItem,9:F2}");
            }

            Console.WriteLine();

            var ring = Array.Find(results, r => r.Name.StartsWith("RingBuffer (producer"));
            var channel = Array.Find(results, r => r.Name.StartsWith("Channel (producer"));
            Console.WriteLine($"Concurrent hand-off: ring is {channel.NanosecondsPerItem / ring.NanosecondsPerItem:F2}x the channel's throughput.");

            if (outputPath is not null)
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(outputPath)));
                System.IO.File.WriteAllText(outputPath, JsonSerializer.Serialize(results,
                    new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"Wrote {outputPath}");
            }

            return 0;
        }

        private static Result Measure(string name, int trials, Func<long> body)
        {
            body(); // warm up JIT and let the thread pool settle

            var best = double.MaxValue;
            long bytes = 0;

            for (var trial = 0; trial < trials; trial++)
            {
                var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
                var started = Stopwatch.GetTimestamp();
                var count = body();
                var elapsed = (Stopwatch.GetTimestamp() - started) / (double)Stopwatch.Frequency;
                var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;

                if (elapsed < best)
                {
                    best = elapsed;
                    bytes = count == 0 ? 0 : allocated / count;
                }
            }

            return new Result(name, Math.Round(best, 6), Math.Round(best * 1e9 / 5_000_000, 2), bytes);
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

        private record Result(string Name, double Seconds, double NanosecondsPerItem, long BytesPerItem);
    }
}
