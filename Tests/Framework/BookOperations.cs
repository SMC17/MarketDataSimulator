using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MarketData.Common.Books;

namespace MarketData.Tests.Framework
{
    public enum BookOperationKind
    {
        Upsert,
        Remove,
        Clear,
    }

    public readonly record struct BookOperation(BookOperationKind Kind, Side Side, int Price, uint Quantity)
    {
        public override string ToString() => Kind switch
        {
            BookOperationKind.Upsert => $"Upsert({Side}, {Price}, {Quantity})",
            BookOperationKind.Remove => $"Remove({Side}, {Price})",
            _ => "Clear()",
        };
    }

    /// <summary>
    /// Generation and shrinking of operation sequences applied to a book.
    /// </summary>
    public static class BookOperations
    {
        /// <summary>
        /// Prices are drawn from a deliberately narrow band so that collisions, evictions and
        /// depth-cap behaviour are hit constantly. Random operations over a wide price space
        /// almost never collide, and a test that never collides never exercises the interesting
        /// paths.
        /// </summary>
        public const int MinPrice = -40;
        public const int MaxPrice = 40;

        public static List<BookOperation> Generate(Random random, int maxLength = 200)
            => Generate(random, maxLength, MinPrice, MaxPrice);

        /// <summary>
        /// Generates over an explicit price band, for books deep enough that the default band
        /// cannot fill them.
        /// </summary>
        /// <remarks>
        /// The default band holds 81 distinct prices, which is plenty to saturate a depth-10 book
        /// but leaves a deep one permanently sparse - and a book that never fills never reaches the
        /// code paths that only exist for full ones.
        /// </remarks>
        public static List<BookOperation> Generate(Random random, int maxLength, int minPrice, int maxPrice)
        {
            var length = random.Next(1, maxLength + 1);
            var operations = new List<BookOperation>(length);

            for (var i = 0; i < length; i++)
                operations.Add(GenerateOne(random, minPrice, maxPrice));

            return operations;
        }

        public static BookOperation GenerateOne(Random random) => GenerateOne(random, MinPrice, MaxPrice);

        public static BookOperation GenerateOne(Random random, int minPrice, int maxPrice)
        {
            var roll = random.NextDouble();
            var side = random.Next(2) == 0 ? Side.Bid : Side.Ask;
            var price = random.Next(minPrice, maxPrice + 1);

            if (roll < 0.70)
                return new BookOperation(BookOperationKind.Upsert, side, price, (uint)random.Next(1, 1000));

            if (roll < 0.97)
                return new BookOperation(BookOperationKind.Remove, side, price, 0);

            return new BookOperation(BookOperationKind.Clear, side, 0, 0);
        }

        /// <summary>
        /// Smaller candidates: drop a contiguous run of operations, or simplify one in place.
        /// Halving runs first converges much faster than removing one element at a time.
        /// </summary>
        public static IEnumerable<List<BookOperation>> Shrink(List<BookOperation> operations)
        {
            for (var chunk = operations.Count / 2; chunk >= 1; chunk /= 2)
            {
                for (var start = 0; start + chunk <= operations.Count; start += chunk)
                {
                    var reduced = new List<BookOperation>(operations.Count - chunk);
                    reduced.AddRange(operations.Take(start));
                    reduced.AddRange(operations.Skip(start + chunk));

                    if (reduced.Count > 0)
                        yield return reduced;
                }
            }

            for (var i = 0; i < operations.Count; i++)
            {
                var operation = operations[i];

                if (operation.Kind == BookOperationKind.Upsert && operation.Quantity > 1)
                {
                    var simplified = new List<BookOperation>(operations);
                    simplified[i] = operation with { Quantity = 1 };
                    yield return simplified;
                }

                if (operation.Price != 0)
                {
                    var simplified = new List<BookOperation>(operations);
                    simplified[i] = operation with { Price = operation.Price / 2 };
                    yield return simplified;
                }
            }
        }

        public static void Apply(IOrderBook book, BookOperation operation)
        {
            switch (operation.Kind)
            {
                case BookOperationKind.Upsert:
                    book.Upsert(operation.Side, operation.Price, operation.Quantity);
                    break;
                case BookOperationKind.Remove:
                    book.Remove(operation.Side, operation.Price);
                    break;
                default:
                    book.Clear();
                    break;
            }
        }

        public static string Describe(List<BookOperation> operations)
        {
            var text = new StringBuilder();
            text.AppendLine($"{operations.Count} operation(s):");

            foreach (var operation in operations)
                text.AppendLine($"  {operation}");

            return text.ToString();
        }
    }
}
