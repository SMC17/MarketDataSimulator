using MarketData.Common.Books;
using MarketData.Common.Feed;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MarketData.Bench
{
    /// <summary>
    /// Measures the multicast feed against a subscriber population.
    /// </summary>
    /// <remarks>
    /// The comparison this exists to make: the unicast benchmark showed mean latency rising
    /// roughly linearly with subscriber count, because the server performs one write per
    /// subscriber per update. Multicast sends once regardless, so the prediction is a latency
    /// curve that is flat in the number of subscribers. This suite measures the same quantity -
    /// publisher transmit to subscriber receipt, on the same monotonic clock - so the two curves
    /// can be laid on top of each other.
    /// </remarks>
    public static class MulticastBenchmark
    {
        public static int Run(string[] args)
        {
            var subscribers = 100;
            var group = "239.7.7.7";
            var port = 31007;
            string redundantGroup = null;
            var redundantPort = 0;
            var @interface = "127.0.0.1";
            var warmup = 5.0;
            var duration = 20.0;
            var receiveBuffer = 256 * 1024;
            var label = "multicast";
            string outputPath = null;

            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--subscribers": subscribers = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--group": group = args[++i]; break;
                    case "--port": port = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--redundant-group": redundantGroup = args[++i]; break;
                    case "--redundant-port": redundantPort = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--interface": @interface = args[++i]; break;
                    case "--warmup": warmup = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--duration": duration = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--receive-buffer": receiveBuffer = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--label": label = args[++i]; break;
                    case "--out": outputPath = args[++i]; break;
                }
            }

            var effectiveRedundantPort = redundantGroup is null ? 0 : redundantPort > 0 ? redundantPort : port;
            var topology = redundantGroup is null
                ? $"{group}:{port}"
                : $"{group}:{port} + {redundantGroup}:{effectiveRedundantPort}";
            Console.WriteLine($"Multicast subscribers={subscribers} groups={topology} " +
                              $"warmup={warmup}s measure={duration}s rcvbuf={receiveBuffer / 1024}KiB");

            var histogram = new LatencyHistogram(32);
            var measuring = 0;
            long measured = 0;

            using var lifetime = new CancellationTokenSource();
            var groupAddress = IPAddress.Parse(group);
            var redundantGroupAddress = redundantGroup is null ? null : IPAddress.Parse(redundantGroup);
            var interfaceAddress = IPAddress.Parse(@interface);

            var clients = new List<MulticastSubscriber>(subscribers);
            var readers = new List<Task>(subscribers);

            for (var i = 0; i < subscribers; i++)
            {
                var index = i;
                var subscriber = new MulticastSubscriber(groupAddress, port, interfaceAddress,
                    _ => new SortedArrayBook(10), receiveBuffer, redundantGroupAddress,
                    redundantPort);

                subscriber.Decoder.MessageObserved += sourceTimestamp =>
                {
                    if (Volatile.Read(ref measuring) == 0)
                        return;

                    histogram.Record(index, LatencyHistogram.ToMicroseconds(Stopwatch.GetTimestamp() - sourceTimestamp));
                    Interlocked.Increment(ref measured);
                };

                clients.Add(subscriber);
                readers.Add(subscriber.ReceiveAsync(lifetime.Token));
            }

            Console.WriteLine($"Joined {clients.Count} subscribers to the group.");

            Thread.Sleep(TimeSpan.FromSeconds(warmup));

            var before = Snapshot(clients);
            Interlocked.Exchange(ref measuring, 1);
            var stopwatch = Stopwatch.StartNew();

            Thread.Sleep(TimeSpan.FromSeconds(duration));

            Interlocked.Exchange(ref measuring, 0);
            var elapsed = stopwatch.Elapsed.TotalSeconds;
            var after = Snapshot(clients);

            var summary = histogram.Summarise(50, 90, 99, 99.9, 99.99);
            var messages = Interlocked.Read(ref measured);

            lifetime.Cancel();

            var report = new MulticastReport(
                label,
                DateTimeOffset.UtcNow,
                group,
                port,
                redundantGroup ?? "",
                effectiveRedundantPort,
                subscribers,
                Math.Round(elapsed, 3),
                messages,
                Math.Round(messages / elapsed, 1),
                Math.Round(messages / elapsed / Math.Max(1, subscribers), 1),
                after.Packets - before.Packets,
                after.Gaps - before.Gaps,
                after.MissedMessages - before.MissedMessages,
                after.Duplicates - before.Duplicates,
                after.LineDivergences - before.LineDivergences,
                after.Malformed - before.Malformed,
                after.IntegrityFailures - before.IntegrityFailures,
                after.Recoveries - before.Recoveries,
                after.SessionChanges - before.SessionChanges,
                after.OldSessionPackets - before.OldSessionPackets,
                after.IgnoredIncrementals - before.IgnoredIncrementals,
                clients.Count(i => i.Decoder.IsStale),
                Math.Round(summary.MeanMs, 4),
                Math.Round(summary.MinMs, 4),
                Math.Round(summary.At(50), 4),
                Math.Round(summary.At(90), 4),
                Math.Round(summary.At(99), 4),
                Math.Round(summary.At(99.9), 4),
                Math.Round(summary.MaxMs, 4));

            Console.WriteLine();
            Console.WriteLine($"RESULT subscribers={report.Subscribers} msgs={report.MessagesReceived} " +
                              $"throughput={report.MessagesPerSecond:N0}/s perSubscriber={report.MessagesPerSecondPerSubscriber:N0}/s");
            Console.WriteLine($"RESULT latency ms: mean={report.MeanMs:0.####} min={report.MinMs:0.####} " +
                              $"p50={report.P50Ms:0.####} p99={report.P99Ms:0.####} max={report.MaxMs:0.####}");
            Console.WriteLine($"RESULT feed: packets={report.Packets} gaps={report.Gaps} missed={report.MissedMessages} " +
                              $"dupes={report.Duplicates} divergence={report.LineDivergences} " +
                              $"malformed={report.Malformed} integrity={report.IntegrityFailures} " +
                              $"recoveries={report.Recoveries} ignored={report.IgnoredIncrementals} " +
                              $"stale={report.StaleSubscribers}");

            if (outputPath is not null)
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(outputPath)));
                System.IO.File.WriteAllText(outputPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"Wrote {outputPath}");
            }

            foreach (var client in clients)
                client.Dispose();

            return 0;
        }

        private static FeedStatistics Snapshot(List<MulticastSubscriber> clients)
        {
            var total = new FeedStatistics();

            foreach (var client in clients)
            {
                var statistics = client.Statistics;
                total.Packets += Interlocked.Read(ref statistics.Packets);
                total.Messages += Interlocked.Read(ref statistics.Messages);
                total.Gaps += Interlocked.Read(ref statistics.Gaps);
                total.MissedMessages += Interlocked.Read(ref statistics.MissedMessages);
                total.Duplicates += Interlocked.Read(ref statistics.Duplicates);
                total.LineDivergences += Interlocked.Read(ref statistics.LineDivergences);
                total.Malformed += Interlocked.Read(ref statistics.Malformed);
                total.IntegrityFailures += Interlocked.Read(ref statistics.IntegrityFailures);
                total.Recoveries += Interlocked.Read(ref statistics.Recoveries);
                total.SessionChanges += Interlocked.Read(ref statistics.SessionChanges);
                total.OldSessionPackets += Interlocked.Read(ref statistics.OldSessionPackets);
                total.IgnoredIncrementals += Interlocked.Read(ref statistics.IgnoredIncrementals);
            }

            return total;
        }

        private record MulticastReport(
            string Label,
            DateTimeOffset TimestampUtc,
            string Group,
            int Port,
            string RedundantGroup,
            int RedundantPort,
            int Subscribers,
            double MeasuredSeconds,
            long MessagesReceived,
            double MessagesPerSecond,
            double MessagesPerSecondPerSubscriber,
            long Packets,
            long Gaps,
            long MissedMessages,
            long Duplicates,
            long LineDivergences,
            long Malformed,
            long IntegrityFailures,
            long Recoveries,
            long SessionChanges,
            long OldSessionPackets,
            long IgnoredIncrementals,
            int StaleSubscribers,
            double MeanMs,
            double MinMs,
            double P50Ms,
            double P90Ms,
            double P99Ms,
            double P999Ms,
            double MaxMs);
    }
}
