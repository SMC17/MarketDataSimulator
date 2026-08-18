using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MarketData.Common
{
    /// <param name="Depth">Number of price levels maintained per side.</param>
    /// <param name="UpdatesPerSecond">
    /// Target rate at which the matching engine produces updates for this instrument. Drives the
    /// dissemination load; the original hard-coded behaviour was roughly one update per second.
    /// Non-positive means "unset" and falls back to that default, because System.Text.Json on
    /// .NET 6 ignores declared defaults for missing constructor parameters.
    /// </param>
    /// <param name="SnapshotProbability">
    /// Fraction of generated updates that are full-book snapshots rather than incrementals.
    /// </param>
    public record Specifications(int Depth, double UpdatesPerSecond = 1.0, double SnapshotProbability = 0.05);
    public record Instrument(int Id, string Symbol, Specifications Specifications);
}
