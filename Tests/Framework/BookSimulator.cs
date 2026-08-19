using MarketData.Common.Books;
using System;
using System.Collections.Generic;

namespace MarketData.Tests.Framework
{
    public enum MutationKind
    {
        None,
        Add,
        Replace,
        Remove,
    }

    /// <summary>One mutation applied to the book, in the form a subscriber would receive it.</summary>
    public readonly record struct Mutation(MutationKind Kind, Side Side, PriceLevel Level)
    {
        public static readonly Mutation None = new Mutation(MutationKind.None, Side.Bid, default);
    }

    /// <summary>
    /// Deterministic random walk over an aggregated book, used as a test fixture.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The live feed is produced by the matching engine, not by this. What this is for is
    /// exercising the aggregated books and the feed decoder in isolation: it generates a stream of
    /// level mutations without dragging in orders, matching or a network, so a test of loss
    /// recovery is a test of loss recovery and nothing else.
    /// </para>
    /// <para>
    /// Deliberately free of I/O, timers and threads: given a seed it produces exactly the same
    /// sequence of mutations every time.
    /// </para>
    /// <para>
    /// Prices are always derived from the current book, extending outwards from the tail or
    /// stepping inside the opposite touch, so the book cannot cross. A generator that drew prices
    /// independently of state would emit crossed books, which no matching engine can produce and
    /// which would make every downstream consumer wrong.
    /// </para>
    /// </remarks>
    public sealed class BookSimulator
    {
        public IOrderBook Book { get; }
        public int PriceBand { get; }

        public BookSimulator(IOrderBook book, int priceBand)
        {
            Book = book ?? throw new ArgumentNullException(nameof(book));
            PriceBand = priceBand;
            _scratch = new PriceLevel[Math.Max(book.Depth, 1)];
        }

        /// <summary>Applies one random mutation and returns what changed.</summary>
        public Mutation Mutate(Random random)
        {
            var side = random.Next(2) == 0 ? Side.Bid : Side.Ask;
            var depth = Book.Depth;
            var count = Book.Count(side);

            if (count > 0)
            {
                var levels = _scratch.AsSpan(0, Book.CopyTo(side, _scratch));

                // Removal pressure rises with occupancy, so the book breathes around its depth
                // rather than pinning to full and emitting nothing but replaces.
                if (random.NextDouble() < count / (double)(depth + 1))
                {
                    var victim = levels[random.Next(levels.Length)];

                    return Book.Remove(side, victim.Price)
                        ? new Mutation(MutationKind.Remove, side, victim)
                        : Mutation.None;
                }

                if (count == depth || random.NextDouble() < 0.5)
                {
                    var target = levels[random.Next(levels.Length)];
                    var quantity = (uint)random.Next(1, 1000);

                    return Book.Upsert(side, target.Price, quantity)
                        ? new Mutation(MutationKind.Replace, side, new PriceLevel(target.Price, quantity))
                        : Mutation.None;
                }
            }

            if (!TryChooseNewPrice(side, random, out var price))
                return Mutation.None;

            var newQuantity = (uint)random.Next(1, 1000);

            return Book.Upsert(side, price, newQuantity)
                ? new Mutation(MutationKind.Add, side, new PriceLevel(price, newQuantity))
                : Mutation.None;
        }

        /// <summary>Rebuilds the book from empty. Occasionally leaves it empty, as a halted book would be.</summary>
        public void Refresh(Random random)
        {
            Book.Clear();

            if (random.NextDouble() > 0.99)
                return;

            for (var i = 0; i < Book.Depth * 2; i++)
                Mutate(random);
        }

        public IReadOnlyList<PriceLevel> ReadSide(Side side)
        {
            var count = Book.CopyTo(side, _scratch);
            var levels = new List<PriceLevel>(count);

            for (var i = 0; i < count; i++)
                levels.Add(_scratch[i]);

            return levels;
        }

        private bool TryChooseNewPrice(Side side, Random random, out int price)
        {
            var step = random.Next(1, 4);
            var opposite = side == Side.Bid ? Side.Ask : Side.Bid;

            if (Book.Count(side) > 0)
            {
                // Extend the ladder outwards from the worst displayed level.
                var levels = _scratch.AsSpan(0, Book.CopyTo(side, _scratch));
                var tail = levels[levels.Length - 1].Price;
                price = side == Side.Bid ? tail - step : tail + step;
            }
            else if (Book.TryGetBest(opposite, out var oppositeTouch))
            {
                // First level on an empty side: step inside the far touch, never through it.
                price = side == Side.Bid ? oppositeTouch.Price - 1 : oppositeTouch.Price + 1;
            }
            else
            {
                price = side == Side.Bid ? -1 : 1;
            }

            // A ladder is only defined over its band. Clamping here keeps every implementation
            // interchangeable rather than making the simulator aware of which one it drives.
            return price >= -PriceBand && price <= PriceBand;
        }

        private readonly PriceLevel[] _scratch;
    }
}
