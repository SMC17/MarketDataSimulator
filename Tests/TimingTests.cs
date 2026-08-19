using System;
using System.Linq;
using MarketData.Common.Time;
using Xunit;

namespace MarketData.Tests
{
    public class ClockUncertaintyTests
    {
        private static UncertainInstant At(long nanoseconds, long uncertainty)
            => new(nanoseconds, uncertainty, TimestampSource.SoftwareMonotonic);

        [Fact]
        public void SeparatedInstantsOrderNormally()
        {
            var early = At(1_000, 10);
            var late = At(2_000, 10);

            Assert.Equal(TemporalOrder.Before, early.CompareTo(late));
            Assert.Equal(TemporalOrder.After, late.CompareTo(early));
        }

        /// <summary>
        /// Overlapping error bars mean the order is not established, and saying so is the point.
        /// </summary>
        /// <remarks>
        /// Two events 100 ns apart, measured by clocks good to ±1 µs, are simply not ordered by
        /// those measurements. Returning Before because one number is smaller would be inventing a
        /// causality claim the data does not support.
        /// </remarks>
        [Fact]
        public void OverlappingUncertaintyIsIndeterminate()
        {
            var a = At(1_000, 1_000);
            var b = At(1_100, 1_000);

            Assert.Equal(TemporalOrder.Indeterminate, a.CompareTo(b));
            Assert.Equal(TemporalOrder.Indeterminate, b.CompareTo(a));

            // Tighten the clocks and the same two events become orderable.
            Assert.Equal(TemporalOrder.Before, At(1_000, 10).CompareTo(At(1_100, 10)));
        }

        [Fact]
        public void TouchingIntervalsAreStillIndeterminate()
        {
            // a.Latest == b.Earliest exactly: not separated, so not ordered.
            var a = At(1_000, 50);
            var b = At(1_100, 50);

            Assert.Equal(TemporalOrder.Indeterminate, a.CompareTo(b));
        }

        [Fact]
        public void AnUnknownInstantOrdersAgainstNothing()
        {
            var unknown = new UncertainInstant(0, 0, TimestampSource.None);

            Assert.False(unknown.IsKnown);
            Assert.Equal(TemporalOrder.Indeterminate, unknown.CompareTo(At(1_000, 1)));
            Assert.Equal(TemporalOrder.Indeterminate, At(1_000, 1).CompareTo(unknown));
        }

        /// <summary>Subtracting two uncertain instants widens the error; it never narrows it.</summary>
        [Fact]
        public void UncertaintiesAddWhenTakingADifference()
        {
            var start = At(1_000, 400);
            var end = At(2_000, 600);

            var duration = end.Since(start);

            Assert.Equal(1_000, duration.Nanoseconds);
            Assert.Equal(1_000, duration.UncertaintyNanoseconds);

            // Exactly at the bound: the interval could be as small as zero, so the ordering is
            // not established - but it cannot actually be negative. Significance is the predicate
            // that matters here, and it is strict for that reason.
            Assert.False(duration.IsSignificant);
            Assert.False(duration.CouldBeNegative);

            // One nanosecond wider and the two events could have happened in either order.
            var wider = At(2_000, 601).Since(At(1_000, 400));
            Assert.True(wider.CouldBeNegative);
            Assert.False(wider.IsSignificant);
        }

        /// <summary>
        /// The check that should gate publishing a latency figure.
        /// </summary>
        /// <remarks>
        /// A 400 ns interval measured with ±50 µs clocks is noise with a number attached. This is
        /// how the type makes that visible instead of letting it be quoted.
        /// </remarks>
        [Fact]
        public void AnIntervalSmallerThanItsErrorBarIsNotSignificant()
        {
            var start = new UncertainInstant(0, 50_000, TimestampSource.SystemWallClock);
            var end = new UncertainInstant(400, 50_000, TimestampSource.SystemWallClock);

            var duration = end.Since(start);

            Assert.Equal(400, duration.Nanoseconds);
            Assert.False(duration.IsSignificant);
            Assert.Contains("not significant", duration.ToString());
        }

        [Fact]
        public void AWellSeparatedIntervalIsSignificant()
        {
            var duration = At(1_000_000, 20).Since(At(0, 20));

            Assert.True(duration.IsSignificant);
            Assert.False(duration.CouldBeNegative);
        }

        /// <summary>Mixing sources loses the source, because the result is no longer either.</summary>
        [Fact]
        public void MixedSourcesDoNotClaimEitherSource()
        {
            var hardware = new UncertainInstant(2_000, 10, TimestampSource.NicHardware);
            var software = new UncertainInstant(1_000, 10, TimestampSource.SoftwareMonotonic);

            Assert.Equal(TimestampSource.None, hardware.Since(software).Source);
            Assert.Equal(TimestampSource.NicHardware,
                hardware.Since(new UncertainInstant(1_000, 10, TimestampSource.NicHardware)).Source);
        }

        [Fact]
        public void TheDetectedClockReportsARealMeasuredBound()
        {
            var clock = UncertainClock.Detect();

            Assert.Equal(TimestampSource.SoftwareMonotonic, clock.Source);
            Assert.True(clock.UncertaintyNanoseconds > 0,
                "a clock claiming zero uncertainty is claiming perfection");

            var first = clock.Now();
            var second = clock.Now();

            Assert.True(second.Nanoseconds >= first.Nanoseconds, "a monotonic clock went backwards");
            Assert.Equal(clock.UncertaintyNanoseconds, first.UncertaintyNanoseconds);
        }

        [Fact]
        public void NegativeUncertaintyIsRejected()
            => Assert.Throws<ArgumentOutOfRangeException>(
                () => new UncertainClock(TimestampSource.SoftwareMonotonic, -1));
    }

    public class ProcessorPlacementTests
    {
        [Fact]
        public void CapabilitiesAreReportedWithoutChangingAnything()
        {
            var capabilities = ProcessorPlacement.Detect();

            Assert.True(capabilities.LogicalProcessors > 0);
            Assert.True(capabilities.NumaNodes >= 1);
            Assert.NotEmpty(capabilities.AllowedProcessors);
            Assert.False(string.IsNullOrWhiteSpace(capabilities.Notes));

            // Allowed processors must be a subset of what exists.
            Assert.True(capabilities.AllowedProcessors.Count <= capabilities.LogicalProcessors);
        }

        [Fact]
        public void PinningToADisallowedProcessorFailsRatherThanPretending()
        {
            Assert.False(ProcessorPlacement.TryPinCurrentThread(-1));
            Assert.False(ProcessorPlacement.TryPinCurrentThread(int.MaxValue));
        }

        [Fact]
        public void PinningTakesEffectWhenItIsPermitted()
        {
            if (!OperatingSystem.IsLinux())
                return;

            var allowed = ProcessorPlacement.AllowedProcessors();
            var target = allowed[0];

            var thread = new System.Threading.Thread(() =>
            {
                if (!ProcessorPlacement.TryPinCurrentThread(target))
                    return;

                // If the pin took, the thread must be on that processor and stay there.
                for (var i = 0; i < 1_000; i++)
                {
                    System.Threading.Thread.SpinWait(1_000);
                    var current = ProcessorPlacement.CurrentProcessor();

                    if (current >= 0)
                        Assert.Equal(target, current);
                }
            })
            { IsBackground = true };

            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        }
    }
}
