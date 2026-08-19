using System;
using System.Collections.Generic;
using System.Linq;
using MarketData.Common.Feed;
using MarketData.Common.Governance;
using MarketData.Common.Reference;
using Xunit;

namespace MarketData.Tests
{
    public class SchemaGovernanceTests
    {
        [Fact]
        public void AFingerprintDependsOnLayoutNotOnDeclarationOrder()
        {
            var a = new Schema(1, new[]
            {
                new MessageSchema("M", 1, new[]
                {
                    new SchemaField("A", FieldType.UInt8, 0, 1),
                    new SchemaField("B", FieldType.UInt32, 1, 4),
                }),
            });

            var b = new Schema(1, new[]
            {
                new MessageSchema("M", 1, new[]
                {
                    new SchemaField("B", FieldType.UInt32, 1, 4),
                    new SchemaField("A", FieldType.UInt8, 0, 1),
                }),
            });

            Assert.Equal(a.Fingerprint, b.Fingerprint);
        }

        [Fact]
        public void AFingerprintChangesWhenAnyPartOfTheLayoutDoes()
        {
            var baseline = FeedSchemas.V2.Fingerprint;

            var movedField = new Schema(2, new[]
            {
                new MessageSchema("Incremental", 1, new[]
                {
                    new SchemaField("MessageType", FieldType.UInt8, 0, 1),
                    new SchemaField("Side", FieldType.UInt8, 1, 1),
                    new SchemaField("InstrumentId", FieldType.UInt32, 2, 4),
                    new SchemaField("Quantity", FieldType.UInt32, 6, 4),
                    new SchemaField("Price", FieldType.Int32, 10, 4),
                }),
            });

            Assert.NotEqual(baseline, movedField.Fingerprint);
        }

        [Fact]
        public void FingerprintComponentsAreLengthDelimited()
        {
            var field = new[] { new SchemaField("F", FieldType.UInt8, 0, 1) };
            var left = new Schema(1, new[] { new MessageSchema("A1", 2, field) });
            var right = new Schema(1, new[] { new MessageSchema("A", 12, field) });

            Assert.NotEqual(left.Fingerprint, right.Fingerprint);
        }

        [Fact]
        public void SchemaConstructionTakesAnImmutableSnapshot()
        {
            var fields = new List<SchemaField>
            {
                new("A", FieldType.UInt8, 0, 1),
            };
            var messages = new List<MessageSchema> { new("M", 1, fields) };
            var schema = new Schema(1, messages);
            var fingerprint = schema.Fingerprint;

            fields.Add(new SchemaField("B", FieldType.UInt8, 1, 1));
            messages.Clear();

            Assert.Single(schema.Messages);
            Assert.Single(schema.Messages[0].Fields);
            Assert.Equal(fingerprint, schema.Fingerprint);
        }

        /// <summary>Overlapping fields are silently destructive, so they are rejected outright.</summary>
        [Fact]
        public void OverlappingFieldsAreRejected()
        {
            var thrown = Assert.Throws<ArgumentException>(() => new Schema(1, new[]
            {
                new MessageSchema("Broken", 1, new[]
                {
                    new SchemaField("First", FieldType.UInt32, 0, 4),
                    new SchemaField("Second", FieldType.UInt32, 2, 4),
                }),
            }));

            Assert.Contains("overlaps", thrown.Message);
        }

        [Fact]
        public void AMismatchedWidthIsRejected()
            => Assert.Throws<ArgumentException>(() => new Schema(1, new[]
            {
                new MessageSchema("Broken", 1, new[]
                {
                    new SchemaField("Wrong", FieldType.UInt32, 0, 2),
                }),
            }));

        // ------------------------------------------------------- compatibility rules

        [Fact]
        public void AppendingAnOptionalFieldWithoutMessageFramingIsBreaking()
        {
            var older = new Schema(1, new[]
            {
                new MessageSchema("M", 1, new[] { new SchemaField("A", FieldType.UInt32, 0, 4) }),
            });

            var newer = new Schema(2, new[]
            {
                new MessageSchema("M", 1, new[]
                {
                    new SchemaField("A", FieldType.UInt32, 0, 4),
                    new SchemaField("B", FieldType.UInt32, 4, 4, Since: 2, Required: false),
                }),
            });

            var report = Compatibility.Compare(older, newer);

            Assert.Equal(CompatibilityKind.Breaking, report.Kind);
            Assert.False(report.CanDeployIndependently);
            Assert.Contains(report.Breaks,
                item => item.Message.Contains("without a length-delimited envelope"));
            Assert.Throws<SchemaCompatibilityException>(
                () => Compatibility.AssertDeployableAgainst(older, newer));
        }

        /// <summary>
        /// An optional field inside the old layout sounds safe and is not: it displaces something.
        /// </summary>
        [Fact]
        public void AddingAnOptionalFieldInsideTheOldLayoutIsBreaking()
        {
            var older = new Schema(1, new[]
            {
                new MessageSchema("M", 1, new[]
                {
                    new SchemaField("A", FieldType.UInt32, 0, 4),
                    new SchemaField("C", FieldType.UInt32, 8, 4),
                }),
            });

            var newer = new Schema(2, new[]
            {
                new MessageSchema("M", 1, new[]
                {
                    new SchemaField("A", FieldType.UInt32, 0, 4),
                    new SchemaField("B", FieldType.UInt32, 4, 4, Since: 2, Required: false),
                    new SchemaField("C", FieldType.UInt32, 8, 4),
                }),
            });

            var report = Compatibility.Compare(older, newer);

            Assert.Equal(CompatibilityKind.Breaking, report.Kind);
            Assert.Contains(report.Breaks, b => b.Field == "B" && b.Message.Contains("inside the previous layout"));
        }

        [Theory]
        [MemberData(nameof(BreakingChanges))]
        public void BreakingChangesAreCaught(string label, Schema older, Schema newer, string expectedText)
        {
            var report = Compatibility.Compare(older, newer);

            Assert.True(report.Kind == CompatibilityKind.Breaking, $"{label} was not reported as breaking");
            Assert.Contains(report.Breaks, b => b.Message.Contains(expectedText, StringComparison.Ordinal));
            Assert.Throws<SchemaCompatibilityException>(() => Compatibility.AssertDeployableAgainst(older, newer));
        }

        public static IEnumerable<object[]> BreakingChanges()
        {
            var baseline = new Schema(1, new[]
            {
                new MessageSchema("M", 1, new[]
                {
                    new SchemaField("A", FieldType.UInt32, 0, 4),
                    new SchemaField("B", FieldType.UInt32, 4, 4),
                }),
                new MessageSchema("N", 2, new[] { new SchemaField("X", FieldType.UInt8, 0, 1) }),
            });

            yield return new object[]
            {
                "removed field", baseline,
                new Schema(2, new[]
                {
                    new MessageSchema("M", 1, new[] { new SchemaField("A", FieldType.UInt32, 0, 4) }),
                    new MessageSchema("N", 2, new[] { new SchemaField("X", FieldType.UInt8, 0, 1) }),
                }),
                "was removed or renamed",
            };

            yield return new object[]
            {
                "moved field", baseline,
                new Schema(2, new[]
                {
                    new MessageSchema("M", 1, new[]
                    {
                        new SchemaField("B", FieldType.UInt32, 0, 4),
                        new SchemaField("A", FieldType.UInt32, 4, 4),
                    }),
                    new MessageSchema("N", 2, new[] { new SchemaField("X", FieldType.UInt8, 0, 1) }),
                }),
                "moved from offset",
            };

            yield return new object[]
            {
                "widened field", baseline,
                new Schema(2, new[]
                {
                    new MessageSchema("M", 1, new[]
                    {
                        new SchemaField("A", FieldType.UInt64, 0, 8),
                        new SchemaField("B", FieldType.UInt32, 8, 4),
                    }),
                    new MessageSchema("N", 2, new[] { new SchemaField("X", FieldType.UInt8, 0, 1) }),
                }),
                "changed from",
            };

            yield return new object[]
            {
                "added required field", baseline,
                new Schema(2, new[]
                {
                    new MessageSchema("M", 1, new[]
                    {
                        new SchemaField("A", FieldType.UInt32, 0, 4),
                        new SchemaField("B", FieldType.UInt32, 4, 4),
                        new SchemaField("C", FieldType.UInt32, 8, 4, Since: 2),
                    }),
                    new MessageSchema("N", 2, new[] { new SchemaField("X", FieldType.UInt8, 0, 1) }),
                }),
                "required field C was added",
            };

            yield return new object[]
            {
                "removed message type", baseline,
                new Schema(2, new[]
                {
                    new MessageSchema("M", 1, new[]
                    {
                        new SchemaField("A", FieldType.UInt32, 0, 4),
                        new SchemaField("B", FieldType.UInt32, 4, 4),
                    }),
                }),
                "was removed",
            };
        }

        [Fact]
        public void ANewUnframedMessageTypeRequiresAReaderFirstCutover()
        {
            var report = Compatibility.Compare(FeedSchemas.V1, FeedSchemas.V2);

            Assert.Equal(CompatibilityKind.ForwardCompatible, report.Kind);
            Assert.False(report.CanDeployIndependently);
            Assert.Contains(report.Breaks,
                item => item.Message.Contains("cannot skip unframed message types"));
            Assert.Throws<SchemaCompatibilityException>(
                () => Compatibility.AssertDeployableAgainst(FeedSchemas.V1, FeedSchemas.V2));
        }

        // ------------------------------------------------------- registry

        [Fact]
        public void NegotiationPicksTheHighestSharedVersion()
        {
            var schema = SchemaRegistry.Default.Negotiate(new[] { 1, 2, 99 });
            Assert.Equal(2, schema.Version);

            Assert.Equal(1, SchemaRegistry.Default.Negotiate(new[] { 1 }).Version);
        }

        [Fact]
        public void NoSharedVersionFailsRatherThanSilentlyDowngrading()
            => Assert.Throws<SchemaNegotiationException>(
                () => SchemaRegistry.Default.Negotiate(new[] { 98, 99 }));

        /// <summary>Equal version numbers do not override a layout mismatch.</summary>
        [Fact]
        public void SameVersionDifferentLayoutIsRejected()
        {
            var thrown = Assert.Throws<SchemaNegotiationException>(
                () => SchemaRegistry.Default.Confirm(2, FeedSchemas.V2.Fingerprint ^ (UInt128)1));

            Assert.Contains("different layout", thrown.Message);
            Assert.Same(FeedSchemas.V2, SchemaRegistry.Default.Confirm(2, FeedSchemas.V2.Fingerprint));
        }

        /// <summary>The declared fixed layouts mirror the encoder.</summary>
        [Fact]
        public void TheDeclaredLayoutMatchesTheEncoder()
        {
            Assert.Equal(5, FeedSchemas.Current.Messages.Count);
            AssertIncremental(FeedSchemas.AddTypeCode, "Add");
            AssertIncremental(FeedSchemas.ReplaceTypeCode, "Replace");
            AssertIncremental(FeedSchemas.RemoveTypeCode, "Remove");

            var snapshot = FeedSchemas.Current.Find(FeedSchemas.SnapshotHeaderTypeCode);
            Assert.NotNull(snapshot);
            Assert.Equal(7, snapshot.Size);
            Assert.Collection(snapshot.Fields.OrderBy(field => field.Offset),
                field => Assert.Equal(("MessageType", FieldType.UInt8, 0, 1),
                    (field.Name, field.Type, field.Offset, field.Length)),
                field => Assert.Equal(("InstrumentId", FieldType.Int32, 1, 4),
                    (field.Name, field.Type, field.Offset, field.Length)),
                field => Assert.Equal(("BidLevels", FieldType.UInt8, 5, 1),
                    (field.Name, field.Type, field.Offset, field.Length)),
                field => Assert.Equal(("AskLevels", FieldType.UInt8, 6, 1),
                    (field.Name, field.Type, field.Offset, field.Length)));

            var heartbeat = FeedSchemas.Current.Find(FeedSchemas.HeartbeatTypeCode);
            Assert.NotNull(heartbeat);
            Assert.Equal(1, heartbeat.Size);
        }

        private static void AssertIncremental(byte typeCode, string name)
        {
            var incremental = FeedSchemas.Current.Find(typeCode);

            Assert.NotNull(incremental);
            Assert.Equal(name, incremental.Name);
            Assert.Equal(FeedProtocol.IncrementalSize, incremental.Size);
            Assert.Collection(incremental.Fields.OrderBy(field => field.Offset),
                field => Assert.Equal(("MessageType", FieldType.UInt8, 0, 1),
                    (field.Name, field.Type, field.Offset, field.Length)),
                field => Assert.Equal(("InstrumentId", FieldType.Int32, 1, 4),
                    (field.Name, field.Type, field.Offset, field.Length)),
                field => Assert.Equal(("Price", FieldType.Int32, 5, 4),
                    (field.Name, field.Type, field.Offset, field.Length)),
                field => Assert.Equal(("Quantity", FieldType.UInt32, 9, 4),
                    (field.Name, field.Type, field.Offset, field.Length)),
                field => Assert.Equal(("Side", FieldType.UInt8, 13, 1),
                    (field.Name, field.Type, field.Offset, field.Length)));
        }
    }

    public class ReferenceDataTests
    {
        private static readonly DateTime Y2020 = new(2020, 1, 1);

        private static InstrumentRecord Record(int id, string symbol, DateTime from, DateTime to,
            ReferenceChangeReason reason = ReferenceChangeReason.Listing, DateTime? recorded = null,
            int tickSize = 1)
            => new(id, symbol, tickSize, 100, "USD", from, to, recorded ?? from, reason);

        [Fact]
        public void LookupIsAnsweredAsOfTheInstantAsked()
        {
            var master = new InstrumentMaster();
            var change = new DateTime(2021, 6, 1);

            master.Amend(Record(1, "OLD", Y2020, change));
            master.Amend(Record(1, "NEW", change, DateTime.MaxValue, ReferenceChangeReason.SymbolChange));

            Assert.Equal("OLD", master.AsOf(1, new DateTime(2020, 5, 1))?.Symbol);
            Assert.Equal("NEW", master.AsOf(1, new DateTime(2022, 5, 1))?.Symbol);

            // Boundary: EffectiveTo is exclusive, EffectiveFrom inclusive.
            Assert.Equal("NEW", master.AsOf(1, change)?.Symbol);
            Assert.Equal("OLD", master.AsOf(1, change.AddTicks(-1))?.Symbol);

            Assert.Null(master.AsOf(1, Y2020.AddDays(-1)));
        }

        /// <summary>Amending an open interval closes it rather than rewriting history.</summary>
        [Fact]
        public void AmendingClosesTheOpenIntervalItSupersedes()
        {
            var master = new InstrumentMaster();
            master.Amend(Record(1, "AAA", Y2020, DateTime.MaxValue));

            var split = new DateTime(2022, 3, 1);
            master.Amend(Record(1, "BBB", split, DateTime.MaxValue, ReferenceChangeReason.SymbolChange));

            Assert.Empty(master.Validate(1));
            Assert.Equal("AAA", master.AsOf(1, split.AddDays(-1))?.Symbol);
            Assert.Equal("BBB", master.AsOf(1, split)?.Symbol);
        }

        /// <summary>
        /// A late correction must not change what we are recorded as having known earlier.
        /// </summary>
        [Fact]
        public void ABitemporalQueryReproducesWhatWasKnownAtTheTime()
        {
            var master = new InstrumentMaster();
            var tradeDay = new DateTime(2021, 4, 5);

            // What we believed on the day.
            master.Amend(Record(1, "XYZ", Y2020, DateTime.MaxValue, tickSize: 1, recorded: Y2020));

            // A correction learned a month later, effective retroactively.
            master.Amend(Record(1, "XYZ", Y2020, DateTime.MaxValue,
                ReferenceChangeReason.Correction, recorded: new DateTime(2021, 5, 10), tickSize: 5));

            // Today's answer uses the correction.
            Assert.Equal(5, master.AsOf(1, tradeDay)?.TickSize);

            // The audit answer - what we could have known on the day - does not.
            Assert.Equal(1, master.AsKnownAt(1, tradeDay, tradeDay)?.TickSize);
            Assert.Equal(5, master.AsKnownAt(1, tradeDay, new DateTime(2021, 6, 1))?.TickSize);
        }

        [Fact]
        public void AnAmendmentDoesNotRewritePriorKnowledge()
        {
            var master = new InstrumentMaster();
            var effective = new DateTime(2021, 1, 1);
            var learned = new DateTime(2021, 6, 1);
            master.Amend(Record(1, "OLD", Y2020, DateTime.MaxValue, recorded: Y2020));
            master.Amend(Record(1, "NEW", effective, DateTime.MaxValue,
                ReferenceChangeReason.SymbolChange, recorded: learned));

            Assert.Equal("OLD", master.AsKnownAt(1, effective.AddMonths(1),
                learned.AddTicks(-1))?.Symbol);
            Assert.Equal("NEW", master.AsOf(1, effective.AddMonths(1))?.Symbol);
        }

        [Fact]
        public void EqualSystemTimesHaveDeterministicEffectiveOrdering()
        {
            var learned = new DateTime(2021, 6, 1);
            var first = Record(1, "OLD", Y2020, DateTime.MaxValue, recorded: learned);
            var second = Record(1, "NEW", new DateTime(2021, 1, 1), DateTime.MaxValue,
                ReferenceChangeReason.SymbolChange, recorded: learned);
            var forward = new InstrumentMaster();
            var reverse = new InstrumentMaster();

            forward.Amend(first);
            forward.Amend(second);
            reverse.Amend(second);
            reverse.Amend(first);

            Assert.Equal(forward.History(1), reverse.History(1));
            Assert.Equal("NEW", reverse.AsOf(1, new DateTime(2021, 2, 1))?.Symbol);
        }

        [Fact]
        public void AmbiguousBitemporalCoordinatesAreRejected()
        {
            var master = new InstrumentMaster();
            var record = Record(1, "A", Y2020, DateTime.MaxValue, recorded: Y2020);
            master.Amend(record);

            Assert.Throws<InvalidOperationException>(() => master.Amend(record with { Symbol = "B" }));
        }

        /// <summary>Symbols are recycled, so a symbol alone does not identify an instrument.</summary>
        [Fact]
        public void ARecycledSymbolResolvesToWhicheverInstrumentHeldItThen()
        {
            var master = new InstrumentMaster();
            var handover = new DateTime(2021, 1, 1);

            master.Amend(Record(1, "ABC", Y2020, handover, ReferenceChangeReason.Delisting));
            master.Amend(Record(2, "ABC", handover, DateTime.MaxValue));

            var before = master.ResolveSymbol("ABC", Y2020.AddMonths(3));
            var after = master.ResolveSymbol("ABC", handover.AddMonths(3));

            Assert.Single(before);
            Assert.Equal(1, before[0].InstrumentId);
            Assert.Single(after);
            Assert.Equal(2, after[0].InstrumentId);
        }

        [Fact]
        public void GapsAndOverlapsAreReported()
        {
            var withGap = new InstrumentMaster();
            withGap.Amend(Record(1, "A", Y2020, new DateTime(2020, 6, 1)));
            withGap.Amend(Record(1, "B", new DateTime(2020, 7, 1), DateTime.MaxValue));
            Assert.Contains(withGap.Validate(1), problem => problem.Contains("nothing covers"));

            var inOrder = new InstrumentMaster();
            inOrder.Amend(Record(1, "A", Y2020, new DateTime(2020, 8, 1)));
            inOrder.Amend(Record(1, "B", new DateTime(2020, 7, 1), DateTime.MaxValue));
            Assert.Empty(inOrder.Validate(1));

            var outOfOrder = new InstrumentMaster();
            outOfOrder.Amend(Record(1, "B", new DateTime(2020, 7, 1), DateTime.MaxValue));
            outOfOrder.Amend(Record(1, "A", Y2020, new DateTime(2020, 8, 1)));
            Assert.Empty(outOfOrder.Validate(1));

            var retroactiveOverlap = new InstrumentMaster();
            retroactiveOverlap.Amend(Record(1, "B", new DateTime(2020, 7, 1),
                DateTime.MaxValue));
            retroactiveOverlap.Amend(Record(1, "A", Y2020, new DateTime(2020, 8, 1),
                recorded: new DateTime(2021, 1, 1)));
            Assert.Contains(retroactiveOverlap.Validate(1),
                problem => problem.Contains("runs to"));

            Assert.Contains(new InstrumentMaster().Validate(99), problem => problem.Contains("no records"));
        }
    }

    public class SessionCalendarTests
    {
        private static readonly DateTime Weekday = new(2026, 8, 19); // a Wednesday

        [Theory]
        [InlineData(3, 0, SessionState.Closed)]
        [InlineData(8, 0, SessionState.PreOpen)]
        [InlineData(9, 29, SessionState.OpeningAuction)]
        [InlineData(10, 0, SessionState.Continuous)]
        [InlineData(15, 59, SessionState.ClosingAuction)]
        [InlineData(17, 0, SessionState.PostClose)]
        [InlineData(21, 0, SessionState.Closed)]
        public void TheDayMovesThroughItsStates(int hour, int minute, SessionState expected)
        {
            var calendar = SessionCalendar.UsEquities();
            Assert.Equal(expected, calendar.StateAt(Weekday.AddHours(hour).AddMinutes(minute)));
        }

        [Fact]
        public void WeekendsAndHolidaysAreClosedAllDay()
        {
            var holiday = new DateTime(2026, 12, 25);
            var calendar = SessionCalendar.UsEquities(new[] { holiday });

            Assert.False(calendar.IsTradingDay(holiday));
            Assert.Equal(SessionState.Closed, calendar.StateAt(holiday.AddHours(11)));

            var saturday = new DateTime(2026, 8, 22);
            Assert.False(calendar.IsTradingDay(saturday));
            Assert.Equal(SessionState.Closed, calendar.StateAt(saturday.AddHours(11)));
        }

        /// <summary>A halt overrides the schedule: the clock says trading, the venue says no.</summary>
        [Fact]
        public void AHaltOverridesTheSchedule()
        {
            var calendar = SessionCalendar.UsEquities();
            var from = Weekday.AddHours(11);
            calendar.AddHalt(new TradingHalt(7, from, from.AddMinutes(30), "news pending"));

            Assert.Equal(SessionState.Halted, calendar.StateAt(from.AddMinutes(5), instrumentId: 7));
            Assert.False(calendar.IsContinuousTrading(from.AddMinutes(5), instrumentId: 7));

            // Only the halted instrument.
            Assert.Equal(SessionState.Continuous, calendar.StateAt(from.AddMinutes(5), instrumentId: 8));

            // And only while it lasts.
            Assert.Equal(SessionState.Continuous, calendar.StateAt(from.AddMinutes(31), instrumentId: 7));
        }

        /// <summary>Only continuous state produces a tradeable quote.</summary>
        [Fact]
        public void OnlyContinuousTradingCountsAsAQuote()
        {
            var calendar = SessionCalendar.UsEquities();

            Assert.True(calendar.IsContinuousTrading(Weekday.AddHours(12)));
            Assert.False(calendar.IsContinuousTrading(Weekday.AddHours(9).AddMinutes(29)));
            Assert.False(calendar.IsContinuousTrading(Weekday.AddHours(15).AddMinutes(59)));
            Assert.False(calendar.IsContinuousTrading(Weekday.AddHours(2)));
        }

        [Fact]
        public void AShortenedDayUsesItsOwnSchedule()
        {
            var shortened = new DateTime(2026, 11, 27);
            var calendar = new SessionCalendar(
                "US-EQUITY",
                SessionCalendar.UsEquities().Windows,
                specialDays: new Dictionary<DateTime, IReadOnlyList<SessionWindow>>
                {
                    [shortened] = new[]
                    {
                        new SessionWindow(SessionState.Continuous, new TimeSpan(9, 30, 0), new TimeSpan(13, 0, 0)),
                    },
                });

            Assert.True(calendar.IsContinuousTrading(shortened.AddHours(12)));
            Assert.Equal(SessionState.Closed, calendar.StateAt(shortened.AddHours(14)));

            // A normal day is unaffected.
            Assert.True(calendar.IsContinuousTrading(Weekday.AddHours(14)));
        }

        [Fact]
        public void TheNextTransitionIsFound()
        {
            var calendar = SessionCalendar.UsEquities();
            var duringContinuous = Weekday.AddHours(10);

            Assert.Equal(Weekday.AddHours(15).AddMinutes(58), calendar.NextTransition(duringContinuous));
        }

        [Fact]
        public void OverlappingWindowsAreRejected()
            => Assert.Throws<ArgumentException>(() => new SessionCalendar("BAD", new[]
            {
                new SessionWindow(SessionState.Continuous, new TimeSpan(9, 0, 0), new TimeSpan(12, 0, 0)),
                new SessionWindow(SessionState.PostClose, new TimeSpan(11, 0, 0), new TimeSpan(14, 0, 0)),
            }));

        [Fact]
        public void InvalidWindowsAndHaltsAreRejected()
        {
            Assert.Throws<ArgumentException>(() => new SessionCalendar("BAD",
                Array.Empty<SessionWindow>()));
            Assert.Throws<ArgumentException>(() => new SessionCalendar("BAD", new[]
            {
                new SessionWindow(SessionState.Continuous, TimeSpan.FromHours(10),
                    TimeSpan.FromHours(9)),
            }));

            var calendar = SessionCalendar.UsEquities();
            Assert.Throws<ArgumentException>(() => calendar.AddHalt(new TradingHalt(0,
                Weekday, Weekday.AddMinutes(1), "invalid")));
        }

        [Fact]
        public void ClosedSessionsTakePrecedenceOverHalts()
        {
            var saturday = new DateTime(2026, 8, 22, 10, 0, 0);
            var calendar = SessionCalendar.UsEquities();
            calendar.AddHalt(new TradingHalt(7, saturday.AddHours(-1), saturday.AddHours(1),
                "news pending"));

            Assert.Equal(SessionState.Closed, calendar.StateAt(saturday, instrumentId: 7));

            var scheduledBreak = new SessionCalendar("TEST", new[]
            {
                new SessionWindow(SessionState.Closed, TimeSpan.FromHours(9), TimeSpan.FromHours(11)),
            });
            scheduledBreak.AddHalt(new TradingHalt(7, Weekday.AddHours(9), Weekday.AddHours(11),
                "news pending"));
            Assert.Equal(SessionState.Closed,
                scheduledBreak.StateAt(Weekday.AddHours(10), instrumentId: 7));
        }
    }
}
