using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace MarketData.Common.Availability
{
    /// <summary>
    /// A latency histogram with fixed logarithmic buckets.
    /// </summary>
    /// <remarks>
    /// Fixed buckets rather than stored samples, because the thing being measured runs at hundreds
    /// of thousands of events per second and keeping samples would allocate proportionally to
    /// traffic. Logarithmic, because latency distributions span orders of magnitude and a linear
    /// bucket wide enough for the tail is useless at the median.
    /// <para>
    /// Quantiles from bucketed data are estimates bounded by bucket width, and the type says so
    /// rather than returning a precise-looking number. Reporting a p99 to the nanosecond from
    /// power-of-two buckets is a false precision that invites exactly the wrong conclusions.
    /// </para>
    /// </remarks>
    public sealed class LatencyHistogram
    {
        private const int BucketCount = 40;
        private readonly long[] _buckets = new long[BucketCount];
        private long _count;
        private long _sum;
        private long _max;

        public long Count => Interlocked.Read(ref _count);
        public long Sum => Interlocked.Read(ref _sum);
        public long Max => Interlocked.Read(ref _max);
        public double Mean => Count == 0 ? 0 : (double)Sum / Count;

        public void Record(long nanoseconds)
        {
            if (nanoseconds < 0)
                return;

            var bucket = BucketFor(nanoseconds);
            Interlocked.Increment(ref _buckets[bucket]);
            Interlocked.Increment(ref _count);
            Interlocked.Add(ref _sum, nanoseconds);

            long observed;
            while (nanoseconds > (observed = Interlocked.Read(ref _max)))
            {
                if (Interlocked.CompareExchange(ref _max, nanoseconds, observed) == observed)
                    break;
            }
        }

        private static int BucketFor(long nanoseconds)
        {
            if (nanoseconds <= 0)
                return 0;

            var bucket = 64 - System.Numerics.BitOperations.LeadingZeroCount((ulong)nanoseconds);
            return Math.Min(bucket, BucketCount - 1);
        }

        /// <summary>
        /// An upper bound on the given quantile.
        /// </summary>
        /// <remarks>
        /// Deliberately an upper bound rather than an interpolation. The true value lies inside the
        /// bucket; naming its top edge is a statement that can be defended, and interpolating
        /// inside it is a guess dressed as a measurement.
        /// </remarks>
        public long QuantileUpperBound(double quantile)
        {
            if (quantile is < 0 or > 1)
                throw new ArgumentOutOfRangeException(nameof(quantile));

            var total = Count;

            if (total == 0)
                return 0;

            var target = (long)Math.Ceiling(quantile * total);
            long seen = 0;

            for (var i = 0; i < BucketCount; i++)
            {
                seen += Interlocked.Read(ref _buckets[i]);

                if (seen >= target)
                    return i == 0 ? 0 : 1L << i;
            }

            return Max;
        }

        public void Reset()
        {
            for (var i = 0; i < BucketCount; i++)
                Interlocked.Exchange(ref _buckets[i], 0);

            Interlocked.Exchange(ref _count, 0);
            Interlocked.Exchange(ref _sum, 0);
            Interlocked.Exchange(ref _max, 0);
        }
    }

    /// <summary>Named counters and histograms for the dissemination path.</summary>
    public sealed class Telemetry
    {
        private readonly ConcurrentDictionary<string, long[]> _counters = new();
        private readonly ConcurrentDictionary<string, LatencyHistogram> _histograms = new();

        public void Increment(string name, long by = 1)
            => Interlocked.Add(ref _counters.GetOrAdd(name, _ => new long[1])[0], by);

        public long Counter(string name)
            => _counters.TryGetValue(name, out var slot) ? Interlocked.Read(ref slot[0]) : 0;

        public LatencyHistogram Histogram(string name)
            => _histograms.GetOrAdd(name, _ => new LatencyHistogram());

        public IReadOnlyDictionary<string, long> Counters()
            => _counters.ToDictionary(entry => entry.Key, entry => Interlocked.Read(ref entry.Value[0]));

        public IEnumerable<string> HistogramNames => _histograms.Keys;
    }

    /// <summary>
    /// A service level objective and the error budget it implies.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The budget is the useful half. An objective on its own invites the question "are we meeting
    /// it?", answered yes or no, which tells you nothing about how much room is left. Expressed as
    /// a budget it becomes a quantity that gets spent, and a burn rate that says whether the
    /// current rate of failure will exhaust it before the window closes.
    /// </para>
    /// <para>
    /// Stated over an explicit window, because "99.9% availability" without one is not a
    /// commitment - the same number means an hour of downtime a year or four minutes a day.
    /// </para>
    /// </remarks>
    public sealed record ServiceLevelObjective(
        string Name,
        double Objective,
        TimeSpan Window,
        long LatencyBudgetNanoseconds = 0)
    {
        /// <summary>Events permitted to fail across the window, given the total seen.</summary>
        public double AllowedFailures(long total) => total * (1 - Objective);

        /// <summary>
        /// Share of the error budget consumed. Above 1 the objective is already missed.
        /// </summary>
        public double BudgetConsumed(long total, long failures)
        {
            var allowed = AllowedFailures(total);
            return allowed <= 0 ? (failures > 0 ? double.PositiveInfinity : 0) : failures / allowed;
        }

        /// <summary>
        /// Whether the budget will run out before the window closes at the current rate.
        /// </summary>
        /// <remarks>
        /// The number worth alerting on. Being inside the objective right now says nothing about
        /// whether the current failure rate is survivable, and a burn rate above 1 means it is not.
        /// </remarks>
        public double BurnRate(long total, long failures, TimeSpan elapsed)
        {
            if (elapsed <= TimeSpan.Zero || Window <= TimeSpan.Zero)
                return 0;

            var consumed = BudgetConsumed(total, failures);
            var windowFraction = elapsed.TotalSeconds / Window.TotalSeconds;

            return windowFraction <= 0 ? 0 : consumed / windowFraction;
        }
    }

    public sealed record SloStatus(
        ServiceLevelObjective Objective,
        long Total,
        long Failures,
        double BudgetConsumed,
        double BurnRate)
    {
        public bool Met => BudgetConsumed <= 1;

        /// <summary>True when the budget will be exhausted before the window ends.</summary>
        public bool WillBreach => BurnRate > 1;

        public override string ToString()
            => $"{Objective.Name}: {Failures:N0}/{Total:N0} failed, " +
               $"{BudgetConsumed * 100:N1}% of budget, burn {BurnRate:N2}x " +
               $"({(Met ? "met" : "MISSED")}{(WillBreach ? ", will breach" : string.Empty)})";
    }
}
