using System;
using System.Diagnostics;

namespace MarketData.Common.Time
{
    /// <summary>Where a timestamp came from, which bounds how much it can be trusted.</summary>
    public enum TimestampSource : byte
    {
        /// <summary>No source; the timestamp is meaningless.</summary>
        None = 0,

        /// <summary>
        /// A monotonic counter read in user space. Excellent for intervals on one host, and says
        /// nothing at all about agreement with any other host.
        /// </summary>
        SoftwareMonotonic = 1,

        /// <summary>System wall clock, disciplined by NTP. Cheap, and typically milliseconds out.</summary>
        SystemWallClock = 2,

        /// <summary>Kernel receive timestamp (SO_TIMESTAMPING). Excludes user-space scheduling delay.</summary>
        KernelSoftware = 3,

        /// <summary>NIC hardware timestamp. Excludes the kernel path as well.</summary>
        NicHardware = 4,

        /// <summary>A PTP-disciplined clock, carrying a servo-reported error bound.</summary>
        PtpDisciplined = 5,
    }

    /// <summary>
    /// An instant together with how wrong it might be.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The central claim of this type is that a bare timestamp is an overstatement. A host reports
    /// nanoseconds and is routinely tens of microseconds - sometimes milliseconds - away from any
    /// other host, and once two timestamps from different machines are compared, that gap decides
    /// the answer. Latency that comes out negative is the usual way people discover this, and by
    /// then the number has already been published.
    /// </para>
    /// <para>
    /// So an instant carries an error bound and a source, and the comparison operators refuse to
    /// answer when the bounds overlap. <see cref="CompareTo"/> returns
    /// <see cref="TemporalOrder.Indeterminate"/> rather than guessing, which is the same discipline
    /// PTP itself uses when it reports offset with an uncertainty rather than a point estimate.
    /// </para>
    /// </remarks>
    public readonly record struct UncertainInstant(
        long Nanoseconds,
        long UncertaintyNanoseconds,
        TimestampSource Source)
    {
        /// <summary>Earliest instant consistent with this measurement.</summary>
        public long Earliest => Nanoseconds - UncertaintyNanoseconds;

        /// <summary>Latest instant consistent with this measurement.</summary>
        public long Latest => Nanoseconds + UncertaintyNanoseconds;

        public bool IsKnown => Source != TimestampSource.None;

        /// <summary>
        /// Orders two instants, or declines to.
        /// </summary>
        /// <remarks>
        /// Two instants whose uncertainty intervals overlap cannot be ordered by any honest
        /// procedure. Returning <see cref="TemporalOrder.Indeterminate"/> forces the caller to
        /// decide what to do about it, which is the point: silently picking one is how a
        /// tie-break becomes a fabricated causality claim.
        /// </remarks>
        public TemporalOrder CompareTo(in UncertainInstant other)
        {
            if (!IsKnown || !other.IsKnown)
                return TemporalOrder.Indeterminate;

            if (Latest < other.Earliest)
                return TemporalOrder.Before;

            if (Earliest > other.Latest)
                return TemporalOrder.After;

            return TemporalOrder.Indeterminate;
        }

        /// <summary>
        /// The interval to <paramref name="later"/>, with the uncertainties combined.
        /// </summary>
        /// <remarks>
        /// Uncertainties add. Subtracting two instants each good to ±1 µs gives a duration good to
        /// ±2 µs, and a one-way latency measured across two hosts inherits the clock offset between
        /// them on top of that. Reporting the difference without the widened bound is the specific
        /// error that produces confident sub-microsecond latency figures from millisecond-grade
        /// clocks.
        /// </remarks>
        public UncertainDuration Since(in UncertainInstant earlier)
            => new(Nanoseconds - earlier.Nanoseconds,
                UncertaintyNanoseconds + earlier.UncertaintyNanoseconds,
                Source == earlier.Source ? Source : TimestampSource.None);

        public override string ToString()
            => IsKnown
                ? $"{Nanoseconds} ns ±{UncertaintyNanoseconds} ({Source})"
                : "unknown";
    }

    public enum TemporalOrder : byte
    {
        Before,
        After,

        /// <summary>The intervals overlap; no ordering is supportable.</summary>
        Indeterminate,
    }

    /// <summary>A duration with an error bound.</summary>
    public readonly record struct UncertainDuration(
        long Nanoseconds,
        long UncertaintyNanoseconds,
        TimestampSource Source)
    {
        /// <summary>
        /// Whether the measured interval is larger than its own error bar.
        /// </summary>
        /// <remarks>
        /// A latency of 400 ns measured with ±50 µs clocks is not a latency of 400 ns; it is noise
        /// with a number attached. This is the check that should gate publishing one.
        /// </remarks>
        public bool IsSignificant => Math.Abs(Nanoseconds) > UncertaintyNanoseconds;

        /// <summary>True when the interval could be negative, i.e. the ordering is not established.</summary>
        public bool CouldBeNegative => Nanoseconds - UncertaintyNanoseconds < 0;

        public override string ToString()
            => $"{Nanoseconds} ns ±{UncertaintyNanoseconds}" + (IsSignificant ? string.Empty : " (not significant)");
    }

    /// <summary>
    /// A clock that reports its own error bound.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On this host only the software sources are real. A PTP-disciplined clock reports its offset
    /// and servo error through the kernel's PHC interface; NIC hardware timestamps require the NIC
    /// and a driver that exposes them; both need privileges and hardware a container does not have.
    /// Rather than pretend, <see cref="Detect"/> reports what is actually available and the
    /// uncertainty is derived from a measured property of this machine instead of an invented
    /// constant.
    /// </para>
    /// <para>
    /// The interface is the deliverable: code that consumes timestamps is written against a source
    /// that admits error, so moving to a PTP-disciplined host changes a construction site and
    /// nothing else.
    /// </para>
    /// </remarks>
    public sealed class UncertainClock
    {
        private readonly long _uncertaintyNanoseconds;
        private readonly long _originTicks;

        public UncertainClock(TimestampSource source, long uncertaintyNanoseconds)
        {
            if (uncertaintyNanoseconds < 0)
                throw new ArgumentOutOfRangeException(nameof(uncertaintyNanoseconds));

            Source = source;
            _uncertaintyNanoseconds = uncertaintyNanoseconds;
            _originTicks = Stopwatch.GetTimestamp();
        }

        public TimestampSource Source { get; }
        public long UncertaintyNanoseconds => _uncertaintyNanoseconds;

        public UncertainInstant Now()
        {
            var ticks = Stopwatch.GetTimestamp() - _originTicks;
            var nanoseconds = (long)(ticks * (1_000_000_000.0 / Stopwatch.Frequency));
            return new UncertainInstant(nanoseconds, _uncertaintyNanoseconds, Source);
        }

        /// <summary>
        /// Builds a clock describing what this host can actually offer.
        /// </summary>
        /// <remarks>
        /// The uncertainty is measured, not assumed: a monotonic counter cannot resolve anything
        /// finer than its own tick, and repeated back-to-back reads reveal the smallest non-zero
        /// step this machine actually produces. That is a floor on the error, not the whole of it,
        /// and it is labelled as such.
        /// </remarks>
        public static UncertainClock Detect()
            => new(TimestampSource.SoftwareMonotonic, MeasureResolutionNanoseconds());

        /// <summary>Smallest non-zero interval this host's monotonic counter reports.</summary>
        public static long MeasureResolutionNanoseconds(int samples = 1_000)
        {
            var nanosecondsPerTick = 1_000_000_000.0 / Stopwatch.Frequency;
            var smallest = long.MaxValue;

            for (var i = 0; i < samples; i++)
            {
                var first = Stopwatch.GetTimestamp();
                long second;

                // Spin until the counter actually moves; the gap is this clock's resolution.
                do
                {
                    second = Stopwatch.GetTimestamp();
                }
                while (second == first);

                var delta = (long)((second - first) * nanosecondsPerTick);

                if (delta > 0 && delta < smallest)
                    smallest = delta;
            }

            return smallest == long.MaxValue ? 1 : smallest;
        }
    }
}
