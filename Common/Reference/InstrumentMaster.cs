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

    /// <summary>Instrument attributes over a half-open effective interval.</summary>
    /// <param name="EffectiveFrom">Inclusive start.</param>
    /// <param name="EffectiveTo">Exclusive end; <see cref="DateTime.MaxValue"/> means current.</param>
    /// <param name="RecordedAt">System time when the fact became known.</param>
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

    /// <summary>Single-writer bitemporal instrument reference data.</summary>
    public sealed class InstrumentMaster
    {
        private readonly Dictionary<int, List<InstrumentRecord>> _byInstrument = new();

        /// <summary>Appends an immutable reference-data observation.</summary>
        public void Amend(InstrumentRecord record)
        {
            ArgumentNullException.ThrowIfNull(record);

            if (record.InstrumentId <= 0 || string.IsNullOrWhiteSpace(record.Symbol) ||
                record.TickSize <= 0 || record.LotSize <= 0 ||
                string.IsNullOrWhiteSpace(record.Currency) ||
                record.EffectiveTo <= record.EffectiveFrom || !Enum.IsDefined(record.Reason))
                throw new ArgumentException("Instrument record is invalid.", nameof(record));

            if (!_byInstrument.TryGetValue(record.InstrumentId, out var history))
                _byInstrument[record.InstrumentId] = history = new List<InstrumentRecord>();

            if (history.Any(existing => existing.RecordedAt == record.RecordedAt &&
                                        existing.EffectiveFrom == record.EffectiveFrom))
            {
                throw new InvalidOperationException(
                    "Reference observations require a unique recorded/effective timestamp pair.");
            }

            history.Add(record);
        }

        /// <summary>The record in force at <paramref name="instant"/>, or null.</summary>
        public InstrumentRecord AsOf(int instrumentId, DateTime instant)
            => _byInstrument.TryGetValue(instrumentId, out var history)
                ? Materialize(history, DateTime.MaxValue)
                    .LastOrDefault(record => record.CoversAt(instant))
                : null;

        /// <summary>Returns effective state using only facts known by <paramref name="asKnownAt"/>.</summary>
        public InstrumentRecord AsKnownAt(int instrumentId, DateTime instant, DateTime asKnownAt)
            => _byInstrument.TryGetValue(instrumentId, out var history)
                ? Materialize(history, asKnownAt).LastOrDefault(record => record.CoversAt(instant))
                : null;

        /// <summary>Resolves every instrument holding a symbol at an effective instant.</summary>
        public IReadOnlyList<InstrumentRecord> ResolveSymbol(string symbol, DateTime instant)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
            return _byInstrument.Values
                .SelectMany(history => Materialize(history, DateTime.MaxValue))
                .Where(record => record.CoversAt(instant) &&
                    string.Equals(record.Symbol, symbol, StringComparison.OrdinalIgnoreCase))
                .OrderBy(record => record.InstrumentId)
                .ToList().AsReadOnly();
        }

        public IReadOnlyList<InstrumentRecord> History(int instrumentId)
            => _byInstrument.TryGetValue(instrumentId, out var history)
                ? Materialize(history, DateTime.MaxValue).AsReadOnly()
                : Array.Empty<InstrumentRecord>();

        public IEnumerable<int> Instruments => _byInstrument.Keys.OrderBy(id => id);

        /// <summary>Reports gaps and overlaps in the current effective timeline.</summary>
        public IReadOnlyList<string> Validate(int instrumentId)
        {
            var problems = new List<string>();

            if (!_byInstrument.TryGetValue(instrumentId, out var history) || history.Count == 0)
            {
                problems.Add($"instrument {instrumentId} has no records");
                return problems;
            }

            var ordered = Materialize(history, DateTime.MaxValue);

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

        private static List<InstrumentRecord> Materialize(List<InstrumentRecord> observations,
            DateTime asKnownAt)
        {
            var timeline = new List<InstrumentRecord>(observations.Count);

            foreach (var observation in observations
                         .Where(record => record.RecordedAt <= asKnownAt)
                         .OrderBy(record => record.RecordedAt)
                         .ThenBy(record => record.EffectiveFrom))
            {
                var record = observation;

                for (var i = timeline.Count - 1; i >= 0; i--)
                {
                    var existing = timeline[i];

                    if (existing.EffectiveFrom == record.EffectiveFrom)
                    {
                        timeline.RemoveAt(i);
                    }
                    else if (existing.EffectiveFrom < record.EffectiveFrom &&
                             existing.EffectiveTo > record.EffectiveFrom)
                    {
                        timeline[i] = existing with { EffectiveTo = record.EffectiveFrom };
                    }
                }

                timeline.Add(record);
            }

            timeline.Sort((left, right) =>
            {
                var effective = left.EffectiveFrom.CompareTo(right.EffectiveFrom);
                return effective != 0 ? effective : left.RecordedAt.CompareTo(right.RecordedAt);
            });
            return timeline;
        }
    }
}
