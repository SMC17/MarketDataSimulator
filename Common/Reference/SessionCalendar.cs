using System;
using System.Collections.Generic;
using System.Linq;

namespace MarketData.Common.Reference
{
    /// <summary>What a venue is doing at a given moment.</summary>
    public enum SessionState
    {
        /// <summary>Outside any session. Orders rejected, no book.</summary>
        Closed,

        /// <summary>Orders accepted and queued; no matching. An indicative price may be published.</summary>
        PreOpen,

        /// <summary>The opening auction is being calculated. Orders may be restricted.</summary>
        OpeningAuction,

        /// <summary>Normal two-sided continuous trading.</summary>
        Continuous,

        /// <summary>Trading suspended for this instrument. Orders may be cancelled but not matched.</summary>
        Halted,

        /// <summary>The closing auction.</summary>
        ClosingAuction,

        /// <summary>After the close: cancels only.</summary>
        PostClose,
    }

    /// <param name="Start">Inclusive, in venue-local time-of-day.</param>
    /// <param name="End">Exclusive.</param>
    public sealed record SessionWindow(SessionState State, TimeSpan Start, TimeSpan End)
    {
        public bool Contains(TimeSpan timeOfDay) => timeOfDay >= Start && timeOfDay < End;
    }

    /// <summary>A halt on one instrument over a closed-open interval.</summary>
    public sealed record TradingHalt(int InstrumentId, DateTime From, DateTime To, string Reason)
    {
        public bool CoversAt(DateTime instant) => instant >= From && instant < To;
    }

    /// <summary>
    /// When a venue trades, and what that implies about the data it emits.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The calendar is not decoration. Most of the ways market data analysis goes wrong are
    /// calendar errors wearing a different hat: a "zero return" that was really a holiday, a
    /// spread that looks impossibly wide because it was sampled during an auction, a book that
    /// appears frozen because the instrument was halted. Each of those is a real number that means
    /// something other than what it appears to mean, and only the calendar can say so.
    /// </para>
    /// <para>
    /// So the state is a first-class input, and <see cref="IsContinuousTrading"/> exists to be
    /// asked before a quote is treated as a quote.
    /// </para>
    /// <para>
    /// Times of day are venue-local and holidays are venue-local dates, because that is how venues
    /// actually define them. Converting to UTC first would need a time zone and would fold the
    /// daylight-saving question into the calendar, where it does not belong.
    /// </para>
    /// </remarks>
    public sealed class SessionCalendar
    {
        private readonly List<SessionWindow> _windows;
        private readonly HashSet<DateTime> _holidays;
        private readonly Dictionary<DateTime, IReadOnlyList<SessionWindow>> _specialDays;
        private readonly List<TradingHalt> _halts = new();

        public SessionCalendar(
            string venue,
            IEnumerable<SessionWindow> windows,
            IEnumerable<DateTime> holidays = null,
            IEnumerable<DayOfWeek> tradingDays = null,
            IDictionary<DateTime, IReadOnlyList<SessionWindow>> specialDays = null)
        {
            Venue = venue;
            _windows = windows.OrderBy(window => window.Start).ToList();
            _holidays = new HashSet<DateTime>((holidays ?? Array.Empty<DateTime>()).Select(d => d.Date));
            _specialDays = specialDays is null
                ? new Dictionary<DateTime, IReadOnlyList<SessionWindow>>()
                : specialDays.ToDictionary(entry => entry.Key.Date, entry => entry.Value);

            TradingDays = new HashSet<DayOfWeek>(tradingDays ?? new[]
            {
                DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
                DayOfWeek.Thursday, DayOfWeek.Friday,
            });

            for (var i = 1; i < _windows.Count; i++)
            {
                if (_windows[i - 1].End > _windows[i].Start)
                    throw new ArgumentException(
                        $"{venue}: session windows overlap at {_windows[i].Start}.", nameof(windows));
            }
        }

        public string Venue { get; }
        public IReadOnlySet<DayOfWeek> TradingDays { get; }
        public IReadOnlyList<SessionWindow> Windows => _windows;

        /// <summary>A US equities-shaped calendar, used by the simulator and the tests.</summary>
        public static SessionCalendar UsEquities(IEnumerable<DateTime> holidays = null) => new(
            "US-EQUITY",
            new[]
            {
                new SessionWindow(SessionState.PreOpen, new TimeSpan(4, 0, 0), new TimeSpan(9, 28, 0)),
                new SessionWindow(SessionState.OpeningAuction, new TimeSpan(9, 28, 0), new TimeSpan(9, 30, 0)),
                new SessionWindow(SessionState.Continuous, new TimeSpan(9, 30, 0), new TimeSpan(15, 58, 0)),
                new SessionWindow(SessionState.ClosingAuction, new TimeSpan(15, 58, 0), new TimeSpan(16, 0, 0)),
                new SessionWindow(SessionState.PostClose, new TimeSpan(16, 0, 0), new TimeSpan(20, 0, 0)),
            },
            holidays);

        public void AddHalt(TradingHalt halt) => _halts.Add(halt);

        public bool IsTradingDay(DateTime date)
            => TradingDays.Contains(date.DayOfWeek) && !_holidays.Contains(date.Date);

        /// <summary>The venue's state for an instrument at a venue-local instant.</summary>
        public SessionState StateAt(DateTime localInstant, int instrumentId = 0)
        {
            // A halt overrides the schedule: the clock says continuous, the venue says no.
            if (_halts.Any(halt => halt.InstrumentId == instrumentId && halt.CoversAt(localInstant)))
                return SessionState.Halted;

            if (!IsTradingDay(localInstant))
                return SessionState.Closed;

            var windows = _specialDays.TryGetValue(localInstant.Date, out var special) ? special : _windows;
            var timeOfDay = localInstant.TimeOfDay;

            foreach (var window in windows)
            {
                if (window.Contains(timeOfDay))
                    return window.State;
            }

            return SessionState.Closed;
        }

        /// <summary>
        /// Whether a two-sided quote at this instant means what a quote normally means.
        /// </summary>
        /// <remarks>
        /// The question worth asking before computing a spread, a mid, or a return. During an
        /// auction the book is indicative and crossed by design; during a halt it is stale; when
        /// closed it is empty. All three produce numbers, and all three of those numbers are
        /// misleading.
        /// </remarks>
        public bool IsContinuousTrading(DateTime localInstant, int instrumentId = 0)
            => StateAt(localInstant, instrumentId) == SessionState.Continuous;

        /// <summary>The next instant at which the state differs, for scheduling transitions.</summary>
        public DateTime NextTransition(DateTime localInstant, int instrumentId = 0)
        {
            var current = StateAt(localInstant, instrumentId);
            var probe = localInstant;

            // Bounded scan: a venue that does not change state within a fortnight is misconfigured,
            // and an unbounded loop here would hang rather than say so.
            var limit = localInstant.AddDays(14);

            while (probe < limit)
            {
                var boundaries = CandidateBoundaries(probe, instrumentId)
                    .Where(candidate => candidate > probe)
                    .OrderBy(candidate => candidate)
                    .ToList();

                foreach (var candidate in boundaries)
                {
                    if (StateAt(candidate, instrumentId) != current)
                        return candidate;
                }

                probe = probe.Date.AddDays(1);
            }

            throw new InvalidOperationException(
                $"{Venue}: no state change within 14 days of {localInstant:O}; the calendar is misconfigured.");
        }

        private IEnumerable<DateTime> CandidateBoundaries(DateTime instant, int instrumentId)
        {
            var date = instant.Date;
            var windows = _specialDays.TryGetValue(date, out var special) ? special : _windows;

            foreach (var window in windows)
            {
                yield return date + window.Start;
                yield return date + window.End;
            }

            foreach (var halt in _halts.Where(h => h.InstrumentId == instrumentId))
            {
                yield return halt.From;
                yield return halt.To;
            }

            yield return date.AddDays(1);
        }
    }
}
