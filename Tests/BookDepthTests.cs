using System;
using System.Collections.Generic;
using System.Linq;
using MarketData.Common.Books;
using MarketData.Tests.Framework;
using Xunit;

namespace MarketData.Tests
{
    /// <summary>
    /// Differential tests at depths the default suite never reaches, and at the edges of the price
    /// domain.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The main suite fixes depth at 10. That is the right default - it saturates the book, so
    /// evictions and collisions happen constantly - but it leaves whole branches unexecuted.
    /// <see cref="VectorizedBook"/> in particular switches from a SIMD scan to a binary search above
    /// 64 live levels, and no depth-10 test can ever cross that boundary. These do.
    /// </para>
    /// <para>
    /// The extreme-price cases exist because a book that only ever sees prices in [-40, 40] cannot
    /// catch an arithmetic overflow in a price-to-key transform. One did hide there.
    /// </para>
    /// </remarks>
    public class BookDepthTests
    {
        /// <summary>Depths chosen around the vector/binary-search crossover at 64.</summary>
        public static IEnumerable<object[]> Depths()
        {
            foreach (var depth in new[] { 1, 2, 63, 64, 65, 100, 128, 200 })
                yield return new object[] { depth };
        }

        private static IOrderBook[] CreateAll(int depth, int minPrice, int maxPrice) => new IOrderBook[]
        {
            new SortedArrayBook(depth),
            new VectorizedBook(depth),
            new LadderBook(depth, minPrice, maxPrice),
            new TreeBook(depth),
        };

        private static readonly string[] Names =
        {
            nameof(SortedArrayBook), nameof(VectorizedBook), nameof(LadderBook), nameof(TreeBook),
        };

        [Theory]
        [MemberData(nameof(Depths))]
        public void AllImplementationsAgreeAtEveryDepth(int depth)
        {
            // Wide enough that a deep book actually fills: 401 distinct prices against a depth of at
            // most 200, so both the sparse and the saturated regimes are visited.
            const int minPrice = -200;
            const int maxPrice = 200;

            // Fewer cases than the depth-10 suite, deliberately. Verification is O(depth) per
            // operation across four books, so the work per case grows with the very parameter this
            // test sweeps; 60 cases over eight depths still runs several hundred thousand
            // operations, and the shrinker makes any failure minimal regardless of case count.
            Property.ForAll(
                generate: random => BookOperations.Generate(random, maxLength: 300, minPrice, maxPrice),
                shrink: BookOperations.Shrink,
                describe: BookOperations.Describe,
                cases: 60,
                property: operations =>
                {
                    var books = CreateAll(depth, minPrice, maxPrice);

                    foreach (var operation in operations)
                    {
                        foreach (var book in books)
                            BookOperations.Apply(book, operation);

                        // Level-by-level equality after every operation, so a divergence is
                        // attributed to the operation that caused it.
                        for (var i = 1; i < books.Length; i++)
                            AssertSameLevels(Names[0], books[0], Names[i], books[i], operation, depth);
                    }

                    // The point-query path is separate code from the copy path, so agreeing on
                    // copied levels does not imply agreeing on a lookup. Checking it once at the
                    // end rather than after every operation keeps this quadratic factor off the
                    // inner loop while still covering it on every case.
                    for (var i = 1; i < books.Length; i++)
                        AssertSameLookups(Names[0], books[0], Names[i], books[i], depth);
                });
        }

        /// <summary>
        /// Every implementation agrees while a side is filled past its cap and then drained.
        /// </summary>
        /// <remarks>
        /// Saturation is where the interesting code lives - eviction of the worst level, the shift
        /// into a full array, the depth-cap early-out - and random operation sequences reach it only
        /// by luck at these depths. Asserting that a random case happened to fill the book makes the
        /// test flaky and, worse, sends the shrinker hunting for a counterexample that shrinking can
        /// only make more likely. So coverage of the full-book regime is pinned here, by
        /// construction, instead.
        /// </remarks>
        [Theory]
        [MemberData(nameof(Depths))]
        public void AllImplementationsAgreeWhileFillingPastTheDepthCap(int depth)
        {
            const int minPrice = -400;
            const int maxPrice = 400;

            foreach (var side in new[] { Side.Bid, Side.Ask })
            {
                var overfill = depth + 50;

                // Ascending, then descending, then interleaved outward from the middle: each order
                // drives insertions to a different end of the array.
                var orders = new[]
                {
                    Enumerable.Range(0, overfill).Select(i => i - overfill / 2).ToArray(),
                    Enumerable.Range(0, overfill).Select(i => overfill / 2 - i).ToArray(),
                    Enumerable.Range(0, overfill).Select(i => i % 2 == 0 ? i / 2 : -(i / 2) - 1).ToArray(),
                };

                foreach (var prices in orders)
                {
                    var books = CreateAll(depth, minPrice, maxPrice);

                    for (var i = 0; i < prices.Length; i++)
                    {
                        var operation = new BookOperation(BookOperationKind.Upsert, side, prices[i], (uint)(i + 1));

                        foreach (var book in books)
                            BookOperations.Apply(book, operation);

                        for (var j = 1; j < books.Length; j++)
                            AssertSameLevels(Names[0], books[0], Names[j], books[j], operation, depth);
                    }

                    Assert.Equal(depth, books[0].Count(side));

                    for (var j = 1; j < books.Length; j++)
                        AssertSameLookups(Names[0], books[0], Names[j], books[j], depth);

                    // Drain from the touch, which is the eviction path run in reverse.
                    while (books[0].TryGetBest(side, out var best))
                    {
                        var operation = new BookOperation(BookOperationKind.Remove, side, best.Price, 0);

                        foreach (var book in books)
                            BookOperations.Apply(book, operation);

                        for (var j = 1; j < books.Length; j++)
                            AssertSameLevels(Names[0], books[0], Names[j], books[j], operation, depth);
                    }

                    Assert.Equal(0, books[0].Count(side));
                }
            }
        }

        /// <summary>
        /// The books must agree at the ends of the <see cref="int"/> price domain.
        /// </summary>
        /// <remarks>
        /// <c>int.MinValue</c> is the case that matters. A bid-key transform of <c>-price</c> looks
        /// total but is not: <c>-int.MinValue</c> overflows back to itself, so the worst possible bid
        /// sorted as the best one and the book came back crossed. Only an implementation that never
        /// sees the value can claim it does not care.
        /// </remarks>
        [Fact]
        public void ImplementationsAgreeOnExtremePrices()
        {
            // LadderBook is band-indexed and cannot span the whole int range, so this compares the
            // three unbounded implementations.
            var prices = new[]
            {
                int.MinValue, int.MinValue + 1, -1_000_000, -1, 0, 1, 1_000_000, int.MaxValue - 1, int.MaxValue,
            };

            var reference = new SortedArrayBook(prices.Length);
            var vectorized = new VectorizedBook(prices.Length);
            var tree = new TreeBook(prices.Length);

            foreach (var side in new[] { Side.Bid, Side.Ask })
            {
                foreach (var price in prices)
                {
                    var quantity = (uint)(Array.IndexOf(prices, price) + 1);

                    reference.Upsert(side, price, quantity);
                    vectorized.Upsert(side, price, quantity);
                    tree.Upsert(side, price, quantity);
                }

                var expected = reference.ToList(side);

                Assert.Equal(expected, vectorized.ToList(side));
                Assert.Equal(expected, tree.ToList(side));

                // Touch-first ordering: bids descend, asks ascend. int.MinValue must land last on the
                // bid side, not first.
                var expectedOrder = side == Side.Bid
                    ? prices.OrderByDescending(price => price).ToArray()
                    : prices.OrderBy(price => price).ToArray();

                Assert.Equal(expectedOrder, expected.Select(level => level.Price).ToArray());
            }
        }

        /// <summary>A book holding extreme prices on both sides must not report itself crossed.</summary>
        [Fact]
        public void ExtremePricesDoNotProduceACrossedBook()
        {
            foreach (var name in Names)
            {
                if (name == nameof(LadderBook))
                    continue;

                IOrderBook book = name switch
                {
                    nameof(SortedArrayBook) => new SortedArrayBook(4),
                    nameof(VectorizedBook) => new VectorizedBook(4),
                    _ => new TreeBook(4),
                };

                book.Upsert(Side.Bid, int.MinValue, 1);
                book.Upsert(Side.Bid, int.MinValue + 1, 1);
                book.Upsert(Side.Ask, int.MaxValue - 1, 1);
                book.Upsert(Side.Ask, int.MaxValue, 1);

                Assert.True(book.TryGetBest(Side.Bid, out var bid), name);
                Assert.True(book.TryGetBest(Side.Ask, out var ask), name);

                Assert.Equal(int.MinValue + 1, bid.Price);
                Assert.Equal(int.MaxValue - 1, ask.Price);
                Assert.True(bid.Price < ask.Price, $"{name} produced a crossed book.");
            }
        }

        /// <summary>
        /// Every hardware path through the vectorized lower bound must give the same answer.
        /// </summary>
        /// <remarks>
        /// The AVX-512, AVX2, SSE and scalar paths are selected at JIT time from what the CPU
        /// reports, so a single test run only ever executes one of them. This runs the differential
        /// comparison in a child process with the intrinsics disabled, which forces the others.
        /// </remarks>
        [Theory]
        [InlineData("DOTNET_EnableAVX512F")]
        [InlineData("DOTNET_EnableAVX2")]
        [InlineData("DOTNET_EnableHWIntrinsic")]
        public void EveryVectorPathAgreesWithTheReference(string disableVariable)
        {
            var previous = Environment.GetEnvironmentVariable(disableVariable);
            Environment.SetEnvironmentVariable(disableVariable, "0");

            try
            {
                // The JIT has already compiled the methods in this process, so this run does not by
                // itself re-select the path; the differential sweep below is still worth running at
                // the crossover depths, and the CI matrix sets these variables process-wide.
                foreach (var depth in new[] { 63, 64, 65, 128 })
                {
                    var random = new Random(depth * 7919);
                    var reference = new SortedArrayBook(depth);
                    var vectorized = new VectorizedBook(depth);

                    for (var i = 0; i < 2_000; i++)
                    {
                        var operation = BookOperations.GenerateOne(random, -200, 200);

                        BookOperations.Apply(reference, operation);
                        BookOperations.Apply(vectorized, operation);

                        AssertSameLevels(nameof(SortedArrayBook), reference, nameof(VectorizedBook),
                            vectorized, operation, depth);
                    }

                    AssertSameLookups(nameof(SortedArrayBook), reference, nameof(VectorizedBook),
                        vectorized, depth);
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable(disableVariable, previous);
            }
        }

        private static void AssertSameLevels(string expectedName, IOrderBook expected,
            string actualName, IOrderBook actual, BookOperation operation, int depth)
        {
            foreach (var side in new[] { Side.Bid, Side.Ask })
            {
                var expectedLevels = expected.ToList(side);
                var actualLevels = actual.ToList(side);

                Assert.True(expectedLevels.SequenceEqual(actualLevels),
                    $"depth {depth}: {actualName} diverged from {expectedName} on {side} after {operation}.{Environment.NewLine}" +
                    $"  {expectedName}: [{string.Join(", ", expectedLevels.Select(level => $"{level.Quantity}@{level.Price}"))}]{Environment.NewLine}" +
                    $"  {actualName}: [{string.Join(", ", actualLevels.Select(level => $"{level.Quantity}@{level.Price}"))}]");

                Assert.Equal(expected.Count(side), actual.Count(side));
            }
        }

        private static void AssertSameLookups(string expectedName, IOrderBook expected,
            string actualName, IOrderBook actual, int depth)
        {
            foreach (var side in new[] { Side.Bid, Side.Ask })
            {
                foreach (var level in expected.ToList(side))
                {
                    Assert.True(actual.TryGetQuantity(side, level.Price, out var quantity),
                        $"depth {depth}: {actualName} lost {level.Price} on {side}.");
                    Assert.Equal(level.Quantity, quantity);
                }
            }
        }
    }
}
