using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;
using MarketData.Common.Books;
using MarketData.Common.Feed;

namespace MarketData.Bench
{
    /// <summary>Wire-path cost with integrity checking enabled.</summary>
    public static class ProtocolBenchmark
    {
        private const ulong Session = 0xBADC0FFEE;

        public static int Run(string[] args)
        {
            var iterations = 1_000_000;
            var trials = 7;
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

            if (iterations <= 0 || trials < 3)
                throw new ArgumentOutOfRangeException(nameof(args), "iterations must be positive and trials at least three");

            var incremental = BuildIncrementalPacket();
            var batch = BuildBatchPacket();
            var snapshot = BuildSnapshotPacket();
            var sealBuffer = (byte[])incremental.Clone();
            var roundTripBuffer = (byte[])incremental.Clone();
            var decoder = NewSynchronizedDecoder();
            var roundTripSequence = decoder.ExpectedSequence;
            ulong sink = 0;

            var cases = new[]
            {
                Measure("seal incremental", incremental.Length, iterations, trials, count =>
                {
                    ulong value = 0;
                    for (var i = 0; i < count; i++)
                    {
                        FeedProtocol.WriteIncremental(sealBuffer.AsSpan(FeedProtocol.HeaderSize),
                            FeedMessageType.Replace, 1, Side.Bid, new PriceLevel(-1, (uint)(i + 1)));
                        FeedProtocol.WriteHeader(sealBuffer, 1, Session, (ulong)i, i);
                        value += sealBuffer[FeedProtocol.ChecksumOffset];
                    }
                    return value;
                }),
                Measure("validate incremental", incremental.Length, iterations, trials,
                    count => Validate(incremental, count)),
                Measure("validate max batch", batch.Length, Math.Max(1, iterations / 8), trials,
                    count => Validate(batch, count)),
                Measure("validate max snapshot", snapshot.Length, Math.Max(1, iterations / 8), trials,
                    count => Validate(snapshot, count)),
                Measure("encode + decode + apply", incremental.Length, iterations, trials, count =>
                {
                    for (var i = 0; i < count; i++)
                    {
                        FeedProtocol.WriteIncremental(roundTripBuffer.AsSpan(FeedProtocol.HeaderSize),
                            FeedMessageType.Replace, 1, Side.Bid, new PriceLevel(-1, (uint)(i + 1)));
                        FeedProtocol.WriteHeader(roundTripBuffer, 1, Session, roundTripSequence++, i);
                        decoder.Consume(roundTripBuffer);
                    }

                    return roundTripSequence;
                }),
            };

            foreach (var result in cases)
                sink ^= (ulong)result.MedianNanoseconds;

            Console.WriteLine($"Feed protocol v{FeedProtocol.Version}; CRC-32C={Crc32C.Implementation}; " +
                $"{iterations:N0} base iterations; median of {trials}");
            Console.WriteLine($"{"Case",27} {"bytes",7} {"median ns",11} {"min ns",10} {"max ns",10} {"M op/s",10} {"GiB/s",8} {"B/op",8}");
            Console.WriteLine(new string('-', 101));

            foreach (var result in cases)
            {
                Console.WriteLine($"{result.Name,27} {result.PacketBytes,7} {result.MedianNanoseconds,11:F1} " +
                    $"{result.MinNanoseconds,10:F1} {result.MaxNanoseconds,10:F1} " +
                    $"{result.MillionOperationsPerSecond,10:F2} {result.GibibytesPerSecond,8:F2} " +
                    $"{result.BytesAllocatedPerOperation,8:F3}");
            }

            var report = new ProtocolReport(
                DateTimeOffset.UtcNow,
                FeedProtocol.Version,
                Crc32C.Implementation,
                Crc32C.IsHardwareAccelerated,
                RuntimeInformation.FrameworkDescription,
                RuntimeInformation.OSDescription,
                RuntimeInformation.ProcessArchitecture.ToString(),
                Environment.ProcessorCount,
                GCSettings.IsServerGC,
                Stopwatch.Frequency,
                iterations,
                trials,
                cases);

            if (outputPath is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
                File.WriteAllText(outputPath, JsonSerializer.Serialize(report,
                    new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"Wrote {outputPath}");
            }

            GC.KeepAlive(sink);
            return 0;
        }

        private static FeedDecoder NewSynchronizedDecoder()
        {
            var decoder = new FeedDecoder(_ => new SortedArrayBook(10));
            var packet = new byte[FeedProtocol.HeaderSize + FeedProtocol.SnapshotSize(1, 1)];
            var offset = FeedProtocol.HeaderSize;
            offset += FeedProtocol.WriteSnapshot(packet.AsSpan(offset), 1,
                new[] { new PriceLevel(-1, 1) }, new[] { new PriceLevel(1, 1) });
            FeedProtocol.WriteHeader(packet.AsSpan(0, offset), 1, Session, 0, 0);
            decoder.Consume(packet.AsSpan(0, offset));
            return decoder;
        }

        private static byte[] BuildIncrementalPacket()
        {
            var packet = new byte[FeedProtocol.HeaderSize + FeedProtocol.IncrementalSize];
            FeedProtocol.WriteIncremental(packet.AsSpan(FeedProtocol.HeaderSize), FeedMessageType.Replace,
                1, Side.Bid, new PriceLevel(-1, 100));
            FeedProtocol.WriteHeader(packet, 1, Session, 1, 1234);
            return packet;
        }

        private static byte[] BuildBatchPacket()
        {
            var count = (FeedProtocol.MaxPacketSize - FeedProtocol.HeaderSize) / FeedProtocol.IncrementalSize;
            var packet = new byte[FeedProtocol.HeaderSize + count * FeedProtocol.IncrementalSize];
            var offset = FeedProtocol.HeaderSize;

            for (var i = 0; i < count; i++)
                offset += FeedProtocol.WriteIncremental(packet.AsSpan(offset), FeedMessageType.Replace,
                    1, Side.Bid, new PriceLevel(-1 - i, (uint)(i + 1)));

            FeedProtocol.WriteHeader(packet, (ushort)count, Session, 1, 1234);
            return packet;
        }

        private static byte[] BuildSnapshotPacket()
        {
            var bids = new PriceLevel[FeedProtocol.MaxSnapshotLevels / 2];
            var asks = new PriceLevel[FeedProtocol.MaxSnapshotLevels - bids.Length];

            for (var i = 0; i < bids.Length; i++)
                bids[i] = new PriceLevel(-1 - i, (uint)(i + 1));
            for (var i = 0; i < asks.Length; i++)
                asks[i] = new PriceLevel(1 + i, (uint)(i + 1));

            var packet = new byte[FeedProtocol.HeaderSize + FeedProtocol.SnapshotSize(bids.Length, asks.Length)];
            var offset = FeedProtocol.HeaderSize;
            offset += FeedProtocol.WriteSnapshot(packet.AsSpan(offset), 1, bids, asks);
            FeedProtocol.WriteHeader(packet.AsSpan(0, offset), 1, Session, 1, 1234);
            return packet;
        }

        private static ulong Validate(byte[] packet, int iterations)
        {
            ulong value = 0;

            for (var i = 0; i < iterations; i++)
            {
                if (!FeedProtocol.TryReadHeader(packet, out var header, out _))
                    throw new InvalidOperationException("prepared packet failed validation");

                var offset = FeedProtocol.HeaderSize;

                for (var message = 0; message < header.MessageCount; message++)
                {
                    var length = FeedProtocol.MessageLength(packet.AsSpan(offset));
                    if (length < 0)
                        throw new InvalidOperationException("prepared message failed validation");
                    offset += length;
                }

                value += (ulong)offset + header.FirstSequence;
            }

            return value;
        }

        private static ProtocolResult Measure(string name, int packetBytes, int iterations, int trials,
            Func<int, ulong> body)
        {
            _ = body(Math.Min(20_000, iterations));
            var samples = new double[trials];
            var allocated = new double[trials];
            ulong sink = 0;

            for (var trial = 0; trial < trials; trial++)
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                var started = Stopwatch.GetTimestamp();
                sink ^= body(iterations);
                var elapsed = Stopwatch.GetTimestamp() - started;
                samples[trial] = elapsed * (1_000_000_000.0 / Stopwatch.Frequency) / iterations;
                allocated[trial] = (GC.GetAllocatedBytesForCurrentThread() - before) / (double)iterations;
            }

            Array.Sort(samples);
            Array.Sort(allocated);
            var median = samples[samples.Length / 2];
            GC.KeepAlive(sink);

            return new ProtocolResult(
                name,
                packetBytes,
                iterations,
                Math.Round(median, 2),
                Math.Round(samples[0], 2),
                Math.Round(samples[^1], 2),
                Math.Round(1_000.0 / median, 3),
                Math.Round(packetBytes / median * 1_000_000_000.0 / (1L << 30), 3),
                Math.Round(allocated[allocated.Length / 2], 4));
        }

        private sealed record ProtocolResult(
            string Name,
            int PacketBytes,
            int Iterations,
            double MedianNanoseconds,
            double MinNanoseconds,
            double MaxNanoseconds,
            double MillionOperationsPerSecond,
            double GibibytesPerSecond,
            double BytesAllocatedPerOperation);

        private sealed record ProtocolReport(
            DateTimeOffset TimestampUtc,
            byte ProtocolVersion,
            string Crc32CImplementation,
            bool HardwareAccelerated,
            string Runtime,
            string OperatingSystem,
            string Architecture,
            int LogicalProcessors,
            bool ServerGc,
            long StopwatchFrequency,
            int BaseIterations,
            int Trials,
            ProtocolResult[] Results);
    }
}
