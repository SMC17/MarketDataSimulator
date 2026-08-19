using System;
using System.Collections.Frozen;
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

    /// <summary>Venue-local session state with holidays, special days, and instrument halts.</summary>
    public sealed class SessionCalendar
    {
        private readonly IReadOnlyList<SessionWindow> _windows;
        private readonly FrozenSet<DateTime> _holidays;
        private readonly Dictionary<DateTime, IReadOnlyList<SessionWindow>> _specialDays;
        private readonly List<TradingHalt> _halts = new();

        public SessionCalendar(
            string venue,
            IEnumerable<SessionWindow> windows,
            IEnumerable<DateTime> holidays = null,
            IEnumerable<DayOfWeek> tradingDays = null,
            IDictionary<DateTime, IReadOnlyList<SessionWindow>> specialDays = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(venue);
            ArgumentNullException.ThrowIfNull(windows);

            Venue = venue;
            var suppliedWindows = windows.ToArray();
            if (suppliedWindows.Any(window => window is null))
                throw new ArgumentException("Session windows cannot be null.", nameof(windows));
            var orderedWindows = suppliedWindows.OrderBy(window => window.Start).ToArray();
            ValidateWindows(orderedWindows, nameof(windows));
            _windows = Array.AsReadOnly(orderedWindows);
            _holidays = (holidays ?? Array.Empty<DateTime>()).Select(date => date.Date).ToFrozenSet();
            _specialDays = specialDays is null
                ? new Dictionary<DateTime, IReadOnlyList<SessionWindow>>()
                : specialDays.ToDictionary(entry => entry.Key.Date, entry =>
                {
                    if (entry.Value is null)
                        throw new ArgumentException("Special-day windows cannot be null.",
                            nameof(specialDays));
                    var supplied = entry.Value.ToArray();
                    if (supplied.Any(window => window is null))
                        throw new ArgumentException("Special-day windows cannot be null.",
                            nameof(specialDays));
                    var copy = supplied.OrderBy(window => window.Start).ToArray();
                    ValidateWindows(copy, nameof(specialDays));
                    return (IReadOnlyList<SessionWindow>)Array.AsReadOnly(copy);
                });

            TradingDays = (tradingDays ?? new[]
            {
                DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
                DayOfWeek.Thursday, DayOfWeek.Friday,
            }).ToFrozenSet();

            if (TradingDays.Count == 0 || TradingDays.Any(day => !Enum.IsDefined(day)))
                throw new ArgumentException("Trading days are invalid.", nameof(tradingDays));
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

        public void AddHalt(TradingHalt halt)
        {
            ArgumentNullException.ThrowIfNull(halt);
            if (halt.InstrumentId <= 0 || halt.To <= halt.From ||
                string.IsNullOrWhiteSpace(halt.Reason))
                throw new ArgumentException("Trading halt is invalid.", nameof(halt));
            _halts.Add(halt);
        }

        public bool IsTradingDay(DateTime date)
            => TradingDays.Contains(date.DayOfWeek) && !_holidays.Contains(date.Date);

        /// <summary>The venue's state for an instrument at a venue-local instant.</summary>
        public SessionState StateAt(DateTime localInstant, int instrumentId = 0)
        {
            if (!IsTradingDay(localInstant))
                return SessionState.Closed;

            var windows = _specialDays.TryGetValue(localInstant.Date, out var special) ? special : _windows;
            var timeOfDay = localInstant.TimeOfDay;

            foreach (var window in windows)
            {
                if (window.Contains(timeOfDay))
                {
                    return window.State != SessionState.Closed &&
                        _halts.Any(halt => halt.InstrumentId == instrumentId &&
                            halt.CoversAt(localInstant))
                        ? SessionState.Halted
                        : window.State;
                }
            }

            return SessionState.Closed;
        }

        /// <summary>Whether the venue is in continuous trading.</summary>
        public bool IsContinuousTrading(DateTime localInstant, int instrumentId = 0)
            => StateAt(localInstant, instrumentId) == SessionState.Continuous;

        /// <summary>The next instant at which the state differs, for scheduling transitions.</summary>
        public DateTime NextTransition(DateTime localInstant, int instrumentId = 0)
        {
            var current = StateAt(localInstant, instrumentId);
            var probe = localInstant;

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

        private static void ValidateWindows(IReadOnlyList<SessionWindow> windows, string parameter)
        {
            if (windows.Count == 0)
                throw new ArgumentException("At least one session window is required.", parameter);

            for (var i = 0; i < windows.Count; i++)
            {
                var window = windows[i];
                if (window is null || !Enum.IsDefined(window.State) || window.Start < TimeSpan.Zero ||
                    window.End > TimeSpan.FromDays(1) || window.End <= window.Start)
                    throw new ArgumentException("Session window is invalid.", parameter);
                if (i > 0 && windows[i - 1].End > window.Start)
                    throw new ArgumentException("Session windows overlap.", parameter);
            }
        }
    }
}
