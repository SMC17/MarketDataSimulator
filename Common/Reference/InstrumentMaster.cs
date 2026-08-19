using System;
using System.Collections.Generic;
using System.Linq;

namespace MarketData.Common.Reference
{
    /// <summary>Why an instrument record changed.</summary>
    public enum ReferenceChangeReason
    {
        Listing,
        SymbolChange,
        TickSizeChange,
        Split,
        ReverseSplit,
        Delisting,
        Correction,
    }

    /// <summary>
    /// An instrument's attributes over one half-open interval of time.
    /// </summary>
    /// <param name="EffectiveFrom">Inclusive start.</param>
    /// <param name="EffectiveTo">
    /// Exclusive end, or <see cref="DateTime.MaxValue"/> while current.
    /// </param>
    /// <param name="RecordedAt">
    /// When this fact was learned, which is not when it took effect. Both are kept; see
    /// <see cref="InstrumentMaster"/>.
    /// </param>
    public sealed record InstrumentRecord(
        int InstrumentId,
        string Symbol,
        int TickSize,
        int LotSize,
        string Currency,
        DateTime EffectiveFrom,
        DateTime EffectiveTo,
        DateTime RecordedAt,
        ReferenceChangeReason Reason)
    {
        public bool CoversAt(DateTime instant) => instant >= EffectiveFrom && instant < EffectiveTo;
    }

    /// <summary>
    /// Effective-dated instrument reference data with point-in-time lookup.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reference data is not a dictionary of current values, and treating it as one is the single
    /// most common way historical analysis goes quietly wrong. A symbol that was reused, a tick
    /// size that changed, a split that repriced everything - each means the correct answer to
    /// "what were this instrument's attributes?" depends on <em>when you are asking about</em>. A
    /// mutable map silently answers every historical question with today's facts.
    /// </para>
    /// <para>
    /// So records are intervals, never overwritten, and lookups take an instant. Amending a fact
    /// closes the old interval rather than editing it.
    /// </para>
    /// <para>
    /// <b>Two time axes, deliberately.</b> <c>EffectiveFrom</c> is when a fact became true in the
    /// world; <c>RecordedAt</c> is when this system learned it. They differ whenever a correction
    /// arrives late, and keeping both is what makes it possible to reproduce a decision made with
    /// the information available at the time - which is what an audit actually asks for. Asking
    /// only "what was true?" is the wrong question when the dispute is about what was knowable.
    /// </para>
    /// </remarks>
    public sealed class InstrumentMaster
    {
        private readonly Dictionary<int, List<InstrumentRecord>> _byInstrument = new();

        /// <summary>Adds a record, closing any open interval it supersedes.</summary>
        public void Amend(InstrumentRecord record)
        {
            ArgumentNullException.ThrowIfNull(record);

            if (record.EffectiveTo <= record.EffectiveFrom)
                throw new ArgumentException("A record must cover a non-empty interval.", nameof(record));

            if (!_byInstrument.TryGetValue(record.InstrumentId, out var history))
                _byInstrument[record.InstrumentId] = history = new List<InstrumentRecord>();

            for (var i = 0; i < history.Count; i++)
            {
                var existing = history[i];

                // An open interval that the new record starts inside gets closed at that point,
                // rather than replaced: the old fact was true for the time it covered, and
                // rewriting it would destroy the ability to reproduce past decisions.
                if (existing.EffectiveFrom < record.EffectiveFrom && existing.EffectiveTo > record.EffectiveFrom)
                    history[i] = existing with { EffectiveTo = record.EffectiveFrom };
            }

            history.Add(record);
            history.Sort((left, right) => left.EffectiveFrom.CompareTo(right.EffectiveFrom));
        }

        /// <summary>The record in force at <paramref name="instant"/>, or null.</summary>
        public InstrumentRecord AsOf(int instrumentId, DateTime instant)
            => _byInstrument.TryGetValue(instrumentId, out var history)
                ? history.LastOrDefault(record => record.CoversAt(instant))
                : null;

        /// <summary>
        /// The record in force at <paramref name="instant"/> as it was <em>known</em> at
        /// <paramref name="asKnownAt"/>.
        /// </summary>
        /// <remarks>
        /// The bitemporal query. Answers "what did we believe about this instrument at the time we
        /// acted?", which is the question an audit or a reconciliation actually poses, and which a
        /// single-axis lookup cannot express at all.
        /// </remarks>
        public InstrumentRecord AsKnownAt(int instrumentId, DateTime instant, DateTime asKnownAt)
            => _byInstrument.TryGetValue(instrumentId, out var history)
                ? history
                    .Where(record => record.CoversAt(instant) && record.RecordedAt <= asKnownAt)
                    .OrderByDescending(record => record.RecordedAt)
                    .FirstOrDefault()
                : null;

        /// <summary>
        /// Resolves a symbol at a point in time.
        /// </summary>
        /// <remarks>
        /// Deliberately not a reverse dictionary. Symbols are recycled - a ticker freed by a
        /// delisting is reassigned to an unrelated company - so a symbol alone does not identify an
        /// instrument, and a lookup that pretends otherwise will confidently return the wrong one.
        /// </remarks>
        public IReadOnlyList<InstrumentRecord> ResolveSymbol(string symbol, DateTime instant)
            => _byInstrument.Values
                .SelectMany(history => history)
                .Where(record => record.CoversAt(instant)
                                 && string.Equals(record.Symbol, symbol, StringComparison.OrdinalIgnoreCase))
                .ToList();

        public IReadOnlyList<InstrumentRecord> History(int instrumentId)
            => _byInstrument.TryGetValue(instrumentId, out var history)
                ? history.ToList()
                : Array.Empty<InstrumentRecord>();

        public IEnumerable<int> Instruments => _byInstrument.Keys;

        /// <summary>
        /// Checks that an instrument's history has no gaps or overlaps.
        /// </summary>
        /// <remarks>
        /// A gap means some instant has no answer; an overlap means it has two. Both are silent
        /// failures at lookup time - the first returns null and the second returns whichever record
        /// happened to sort last - so the structure is validated directly instead.
        /// </remarks>
        public IReadOnlyList<string> Validate(int instrumentId)
        {
            var problems = new List<string>();

            if (!_byInstrument.TryGetValue(instrumentId, out var history) || history.Count == 0)
            {
                problems.Add($"instrument {instrumentId} has no records");
                return problems;
            }

            var ordered = history.OrderBy(record => record.EffectiveFrom).ToList();

            for (var i = 1; i < ordered.Count; i++)
            {
                var previous = ordered[i - 1];
                var current = ordered[i];

                if (previous.EffectiveTo > current.EffectiveFrom)
                {
                    problems.Add($"instrument {instrumentId}: {previous.Symbol} runs to " +
                                 $"{previous.EffectiveTo:O} but {current.Symbol} starts at " +
                                 $"{current.EffectiveFrom:O}");
                }
                else if (previous.EffectiveTo < current.EffectiveFrom)
                {
                    problems.Add($"instrument {instrumentId}: nothing covers " +
                                 $"{previous.EffectiveTo:O} to {current.EffectiveFrom:O}");
                }
            }

            return problems;
        }
    }
}
