using System;
using System.Diagnostics;
using System.Threading;

namespace MarketData.Bench
{
    /// <summary>
    /// Sharded fixed-bucket latency histogram. Percentiles have to survive tens of millions of
    /// samples without the recorder itself becoming the bottleneck, so recording is a bounds check
    /// and one interlocked increment, and the shards keep subscriber threads off each other's
    /// cache lines.
    /// </summary>
    public sealed class LatencyHistogram
    {
        // Resolution deliberately varies with magnitude: sub-millisecond results are the interesting
        // ones and need microsecond precision, while a saturated run only needs to be placed roughly.
        private const int FineBuckets = 10_000;      // 1us steps      -> 0 .. 10ms
        private const int MediumBuckets = 9_900;     // 100us steps    -> 10ms .. 1s
        private const int CoarseBuckets = 5_900;     // 10ms steps     -> 1s .. 60s
        private const int TotalBuckets = FineBuckets + MediumBuckets + CoarseBuckets + 1; // + overflow

        private const long FineLimitUs = 10_000;
        private const long MediumLimitUs = 1_000_000;
        private const long CoarseLimitUs = 60_000_000;

        private readonly long[][] _shards;
        private readonly long[] _counts;
        private readonly long[] _sums;
        private readonly long[] _minimums;
        private readonly long[] _maximums;
        private readonly int _shardMask;

        public LatencyHistogram(int shards = 16)
        {
            var power = 1;
            while (power < shards)
                power <<= 1;

            _shardMask = power - 1;
            _shards = new long[power][];
            _counts = new long[power * 8];
            _sums = new long[power * 8];
            _minimums = new long[power * 8];
            _maximums = new long[power * 8];

            for (var i = 0; i < power; i++)
            {
                _shards[i] = new long[TotalBuckets];
                _minimums[i * 8] = long.MaxValue;
            }
        }

        public static long ToMicroseconds(long stopwatchTicks)
            => (long)(stopwatchTicks * (1_000_000.0 / Stopwatch.Frequency));

        public void Record(int shard, long microseconds)
        {
            if (microseconds < 0)
                microseconds = 0;

            shard &= _shardMask;

            Interlocked.Increment(ref _shards[shard][BucketOf(microseconds)]);

            var slot = shard * 8;
            Interlocked.Increment(ref _counts[slot]);
            Interlocked.Add(ref _sums[slot], microseconds);

            if (microseconds < Volatile.Read(ref _minimums[slot]))
                Volatile.Write(ref _minimums[slot], microseconds);

            if (microseconds > Volatile.Read(ref _maximums[slot]))
                Volatile.Write(ref _maximums[slot], microseconds);
        }

        private static int BucketOf(long microseconds)
        {
            if (microseconds < FineLimitUs)
                return (int)microseconds;

            if (microseconds < MediumLimitUs)
                return FineBuckets + (int)((microseconds - FineLimitUs) / 100);

            if (microseconds < CoarseLimitUs)
                return FineBuckets + MediumBuckets + (int)((microseconds - MediumLimitUs) / 10_000);

            return TotalBuckets - 1;
        }

        private static long BucketMidpointUs(int bucket)
        {
            if (bucket < FineBuckets)
                return bucket;

            if (bucket < FineBuckets + MediumBuckets)
                return FineLimitUs + (bucket - FineBuckets) * 100 + 50;

            if (bucket < FineBuckets + MediumBuckets + CoarseBuckets)
                return MediumLimitUs + (bucket - FineBuckets - MediumBuckets) * 10_000L + 5_000;

            return CoarseLimitUs;
        }

        public LatencySummary Summarise(params double[] percentiles)
        {
            var merged = new long[TotalBuckets];
            long count = 0, sum = 0, minimum = long.MaxValue, maximum = 0;

            for (var shard = 0; shard < _shards.Length; shard++)
            {
                var buckets = _shards[shard];

                for (var bucket = 0; bucket < TotalBuckets; bucket++)
                    merged[bucket] += Volatile.Read(ref buckets[bucket]);

                var slot = shard * 8;
                var shardCount = Volatile.Read(ref _counts[slot]);

                if (shardCount == 0)
                    continue;

                count += shardCount;
                sum += Volatile.Read(ref _sums[slot]);
                minimum = Math.Min(minimum, Volatile.Read(ref _minimums[slot]));
                maximum = Math.Max(maximum, Volatile.Read(ref _maximums[slot]));
            }

            var results = new double[percentiles.Length];

            if (count == 0)
                return new LatencySummary(0, 0, 0, 0, percentiles, results);

            for (var p = 0; p < percentiles.Length; p++)
            {
                var target = (long)Math.Ceiling(percentiles[p] / 100.0 * count);
                target = Math.Max(1, Math.Min(count, target));

                long running = 0;

                for (var bucket = 0; bucket < TotalBuckets; bucket++)
                {
                    running += merged[bucket];

                    if (running >= target)
                    {
                        results[p] = BucketMidpointUs(bucket) / 1000.0;
                        break;
                    }
                }
            }

            return new LatencySummary(count, sum / (double)count / 1000.0, minimum / 1000.0, maximum / 1000.0, percentiles, results);
        }
    }

    public record LatencySummary(long Count, double MeanMs, double MinMs, double MaxMs, double[] Percentiles, double[] PercentileMs)
    {
        public double At(double percentile)
        {
            for (var i = 0; i < Percentiles.Length; i++)
                if (Math.Abs(Percentiles[i] - percentile) < 1e-9)
                    return PercentileMs[i];

            return double.NaN;
        }
    }
}
