using System;

namespace MarketData.Common.Books
{
    public static class BookFactory
    {
        /// <summary>
        /// Default chosen by measurement, not by asymptotics. At the display depths a market data
        /// feed actually publishes, the flat array is fastest on both the update path and the
        /// publish path, and allocates nothing on either. See BENCHMARKS.md.
        /// </summary>
        public const string Default = "SortedArray";

        public static IOrderBook Create(string implementation, int depth, int priceBand)
        {
            return (implementation ?? Default).ToLowerInvariant() switch
            {
                "sortedarray" or "array" => new SortedArrayBook(depth),
                "vectorized" or "simd" => new VectorizedBook(depth),
                "ladder" => new LadderBook(depth, -priceBand, priceBand),
                "tree" => new TreeBook(depth),
                _ => throw new ArgumentException(
                    $"Unknown book implementation '{implementation}'. Expected SortedArray, Vectorized, Ladder or Tree.",
                    nameof(implementation)),
            };
        }
    }
}
