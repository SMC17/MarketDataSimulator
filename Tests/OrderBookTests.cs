using System;
using System.Collections.Generic;
using System.Linq;
using MarketData.Common.Books;
using MarketData.Tests.Framework;
using Xunit;

namespace MarketData.Tests
{
    public class OrderBookTests
    {
        private const int Depth = 10;

        public static IEnumerable<object[]> Implementations()
        {
            yield return new object[] { nameof(SortedArrayBook) };
            yield return new object[] { nameof(LadderBook) };
            yield return new object[] { nameof(TreeBook) };
        }

        private static IOrderBook Create(string implementation, int depth = Depth) => implementation switch
        {
            nameof(SortedArrayBook) => new SortedArrayBook(depth),
            nameof(LadderBook) => new LadderBook(depth, BookOperations.MinPrice, BookOperations.MaxPrice),
            nameof(TreeBook) => new TreeBook(depth),
            _ => throw new ArgumentOutOfRangeException(nameof(implementation), implementation, null),
        };

        // ---------------------------------------------------------------- semantics

        [Theory]
        [MemberData(nameof(Implementations))]
        public void EmptyBookHasNoTouch(string implementation)
        {
            var book = Create(implementation);

            Assert.False(book.TryGetBest(Side.Bid, out _));
            Assert.False(book.TryGetBest(Side.Ask, out _));
            Assert.Equal(0, book.Count(Side.Bid));
            Assert.Null(book.Spread());
        }

        [Theory]
        [MemberData(nameof(Implementations))]
        public void BestBidIsHighestAndBestAskIsLowest(string implementation)
        {
            var book = Create(implementation);

            foreach (var price in new[] { 5, 9, 7 })
            {
                book.Upsert(Side.Bid, -price, 100);
                book.Upsert(Side.Ask, price, 100);
            }

            Assert.True(book.TryGetBest(Side.Bid, out var bid));
            Assert.True(book.TryGetBest(Side.Ask, out var ask));
            Assert.Equal(-5, bid.Price);
            Assert.Equal(5, ask.Price);
            Assert.Equal(10, book.Spread());
        }

        [Theory]
        [MemberData(nameof(Implementations))]
        public void UpsertReplacesQuantityAtSamePrice(string implementation)
        {
            var book = Create(implementation);

            Assert.True(book.Upsert(Side.Bid, 3, 100));
            Assert.True(book.Upsert(Side.Bid, 3, 250));
            Assert.False(book.Upsert(Side.Bid, 3, 250)); // idempotent write is not a change

            Assert.Equal(1, book.Count(Side.Bid));
            Assert.True(book.TryGetBest(Side.Bid, out var best));
            Assert.Equal(250u, best.Quantity);
        }

        [Theory]
        [MemberData(nameof(Implementations))]
        public void ZeroQuantityDeletesTheLevel(string implementation)
        {
            var book = Create(implementation);
            book.Upsert(Side.Ask, 4, 100);

            Assert.True(book.Upsert(Side.Ask, 4, 0));
            Assert.Equal(0, book.Count(Side.Ask));
        }

        [Theory]
        [MemberData(nameof(Implementations))]
        public void DepthCapEvictsTheWorstLevel(string implementation)
        {
            var book = Create(implementation, depth: 3);

            // Bids at 10, 9, 8 - best is 10, worst is 8.
            foreach (var price in new[] { 10, 9, 8 })
                book.Upsert(Side.Bid, price, 100);

            // A better bid evicts the worst.
            Assert.True(book.Upsert(Side.Bid, 11, 100));
            Assert.Equal(3, book.Count(Side.Bid));
            Assert.Equal(new[] { 11, 10, 9 }, book.ToList(Side.Bid).Select(i => i.Price));

            // A worse bid is outside the displayed window and changes nothing.
            Assert.False(book.Upsert(Side.Bid, 1, 100));
            Assert.Equal(new[] { 11, 10, 9 }, book.ToList(Side.Bid).Select(i => i.Price));
        }

        [Theory]
        [MemberData(nameof(Implementations))]
        public void RemovingTheTouchPromotesTheNextLevel(string implementation)
        {
            var book = Create(implementation);
            book.Upsert(Side.Ask, 5, 100);
            book.Upsert(Side.Ask, 6, 100);

            Assert.True(book.Remove(Side.Ask, 5));
            Assert.True(book.TryGetBest(Side.Ask, out var best));
            Assert.Equal(6, best.Price);
        }

        [Theory]
        [MemberData(nameof(Implementations))]
        public void RemovingAbsentLevelReportsNoChange(string implementation)
        {
            var book = Create(implementation);
            Assert.False(book.Remove(Side.Bid, 7));
        }

        // ---------------------------------------------------------------- invariants

        [Theory]
        [MemberData(nameof(Implementations))]
        public void LevelsStayOrderedAndBoundedUnderRandomOperations(string implementation)
        {
            Property.ForAll(
                generate: random => BookOperations.Generate(random),
                shrink: BookOperations.Shrink,
                describe: BookOperations.Describe,
                property: operations =>
                {
                    var book = Create(implementation);

                    foreach (var operation in operations)
                    {
                        BookOperations.Apply(book, operation);
                        AssertInvariants(book);
                    }
                });
        }

        private static void AssertInvariants(IOrderBook book)
        {
            foreach (var side in new[] { Side.Bid, Side.Ask })
            {
                var levels = book.ToList(side);

                Assert.True(levels.Count <= book.Depth, "depth cap exceeded");
                Assert.Equal(book.Count(side), levels.Count);

                for (var i = 1; i < levels.Count; i++)
                {
                    Assert.True(SideOrder.IsBetter(side, levels[i - 1].Price, levels[i].Price),
                        $"levels not ordered touch-first on {side}: {levels[i - 1].Price} then {levels[i].Price}");
                }

                Assert.Equal(levels.Count, levels.Select(i => i.Price).Distinct().Count());
                Assert.DoesNotContain(levels, level => level.Quantity == 0);

                if (levels.Count > 0)
                {
                    Assert.True(book.TryGetBest(side, out var best));
                    Assert.Equal(levels[0], best);
                }
            }
        }

        // ---------------------------------------------------------------- differential

        [Fact]
        public void AllImplementationsAgreeUnderRandomOperations()
        {
            Property.ForAll(
                generate: random => BookOperations.Generate(random, maxLength: 400),
                shrink: BookOperations.Shrink,
                describe: BookOperations.Describe,
                property: operations =>
                {
                    var books = new (string Name, IOrderBook Book)[]
                    {
                        (nameof(SortedArrayBook), Create(nameof(SortedArrayBook))),
                        (nameof(LadderBook), Create(nameof(LadderBook))),
                        (nameof(TreeBook), Create(nameof(TreeBook))),
                    };

                    foreach (var operation in operations)
                    {
                        foreach (var (_, book) in books)
                            BookOperations.Apply(book, operation);

                        // Compared after every single operation rather than at the end, so a
                        // divergence is attributed to the operation that caused it.
                        var reference = books[0];

                        for (var i = 1; i < books.Length; i++)
                        {
                            AssertSameState(reference.Name, reference.Book, books[i].Name, books[i].Book, operation);
                        }
                    }
                });
        }

        private static void AssertSameState(string expectedName, IOrderBook expected,
            string actualName, IOrderBook actual, BookOperation operation)
        {
            foreach (var side in new[] { Side.Bid, Side.Ask })
            {
                var expectedLevels = expected.ToList(side);
                var actualLevels = actual.ToList(side);

                Assert.True(expectedLevels.SequenceEqual(actualLevels),
                    $"{expectedName} and {actualName} diverged on {side} after {operation}.\n" +
                    $"  {expectedName}: [{string.Join(", ", expectedLevels)}]\n" +
                    $"  {actualName}: [{string.Join(", ", actualLevels)}]");
            }
        }

        // ---------------------------------------------------------------- ladder specifics

        [Fact]
        public void LadderRejectsPricesOutsideItsBand()
        {
            var book = new LadderBook(depth: 4, minPrice: -10, maxPrice: 10);

            Assert.Throws<ArgumentOutOfRangeException>(() => book.Upsert(Side.Bid, 11, 100));
            Assert.Throws<ArgumentOutOfRangeException>(() => book.Upsert(Side.Bid, -11, 100));
        }

        [Fact]
        public void LadderHandlesLevelsSpanningManyBitsetWords()
        {
            // The bit-scan walks 64 price slots per word; a band several words wide with levels at
            // both extremes exercises the word-crossing paths in NextSet/PreviousSet.
            var book = new LadderBook(depth: 8, minPrice: 0, maxPrice: 500);

            foreach (var price in new[] { 0, 63, 64, 65, 127, 128, 400, 500 })
                book.Upsert(Side.Ask, price, 10);

            Assert.Equal(new[] { 0, 63, 64, 65, 127, 128, 400, 500 },
                book.ToList(Side.Ask).Select(i => i.Price));

            Assert.True(book.Remove(Side.Ask, 0));
            Assert.True(book.TryGetBest(Side.Ask, out var best));
            Assert.Equal(63, best.Price);
        }

        [Theory]
        [MemberData(nameof(Implementations))]
        public void ClearEmptiesBothSides(string implementation)
        {
            var book = Create(implementation);
            book.Upsert(Side.Bid, -1, 5);
            book.Upsert(Side.Ask, 1, 5);

            book.Clear();

            Assert.Equal(0, book.Count(Side.Bid));
            Assert.Equal(0, book.Count(Side.Ask));
            Assert.False(book.TryGetBest(Side.Ask, out _));
        }
    }
}
