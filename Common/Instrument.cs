namespace MarketData.Common
{
    /// <param name="Depth">Number of price levels maintained per side.</param>
    /// <param name="UpdatesPerSecond">
    /// Target rate at which the matching engine produces updates for this instrument.
    /// </param>
    /// <param name="SnapshotProbability">
    /// Fraction of generated updates that are full-book snapshots rather than incrementals.
    /// </param>
    public record Specifications(int Depth, double UpdatesPerSecond = 1.0, double SnapshotProbability = 0.05);
    public record Instrument(int Id, string Symbol, Specifications Specifications);
}
