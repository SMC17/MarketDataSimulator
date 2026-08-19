using System;
using System.Collections.Generic;
using System.Linq;
using MarketData.Common.Books;
using MarketData.Tests.Framework;
using Xunit;

namespace MarketData.Tests
{
    /// <summary>
    /// Properties of the generated feed itself, independent of any transport.
    /// </summary>
    public class FeedIntegrityTests
    {
        private const int Depth = 10;
        private const int PriceBand = 512;

        public static IEnumerable<object[]> Implementations()
        {
            yield return new object[] { "SortedArray" };
            yield return new object[] { "Ladder" };
            yield return new object[] { "Tree" };
        }

        private static BookSimulator Create(string implementation) =>
            new BookSimulator(BookFactory.Create(implementation, Depth, PriceBand), PriceBand);

        /// <summary>
        /// The defining property of an incremental feed: a subscriber that starts from a snapshot
        /// and applies every subsequent incremental must end up with exactly the publisher's book.
        /// </summary>
        /// <remarks>
        /// If this does not hold, every downstream consumer is silently trading on a book that
        /// does not exist, and nothing else the system does can compensate. It is checked after
        /// every single mutation rather than at the end, so a divergence is attributed to the
        /// mutation that caused it rather than discovered thousands of updates later.
        /// </remarks>
        [Theory]
        [MemberData(nameof(Implementations))]
        public void IncrementalStreamReconstructsThePublisherBook(string implementation)
        {
            Property.ForAll(
                generate: random => random.Next(1, 3000),
                shrink: updates => updates > 1 ? new[] { updates / 2, updates - 1 } : Array.Empty<int>(),
                describe: updates => $"{updates} update(s)",
                cases: 40,
                property: updates =>
                {
                    var publisher = Create(implementation);
                    var subscriber = BookFactory.Create(implementation, Depth, PriceBand);
                    var random = new Random(1234);

                    for (var i = 0; i < updates; i++)
                    {
                        var mutation = publisher.Mutate(random);

                        switch (mutation.Kind)
                        {
                            case MutationKind.Add:
                            case MutationKind.Replace:
                                subscriber.Upsert(mutation.Side, mutation.Level.Price, mutation.Level.Quantity);
                                break;
                            case MutationKind.Remove:
                                subscriber.Remove(mutation.Side, mutation.Level.Price);
                                break;
                        }

                        AssertSameBook(publisher.Book, subscriber, i, mutation);
                    }
                });
        }

        /// <summary>
        /// A snapshot must be a complete description of the book: replacing a subscriber's state
        /// with it, then continuing to apply incrementals, must stay in step. This is the recovery
        /// path a subscriber takes after a gap.
        /// </summary>
        [Theory]
        [MemberData(nameof(Implementations))]
        public void SnapshotRecoveryResynchronisesASubscriber(string implementation)
        {
            var publisher = Create(implementation);
            var subscriber = BookFactory.Create(implementation, Depth, PriceBand);
            var random = new Random(99);

            for (var round = 0; round < 200; round++)
            {
                // Drift: the subscriber misses this run of updates entirely.
                for (var i = 0; i < 25; i++)
                    publisher.Mutate(random);

                // Recovery: replace state wholesale from a snapshot.
                subscriber.Clear();

                foreach (var side in new[] { Side.Bid, Side.Ask })
                {
                    foreach (var level in publisher.ReadSide(side))
                        subscriber.Upsert(side, level.Price, level.Quantity);
                }

                AssertSameBook(publisher.Book, subscriber, round, Mutation.None);

                // And back in step for subsequent incrementals.
                for (var i = 0; i < 25; i++)
                {
                    var mutation = publisher.Mutate(random);

                    if (mutation.Kind == MutationKind.Remove)
                        subscriber.Remove(mutation.Side, mutation.Level.Price);
                    else if (mutation.Kind != MutationKind.None)
                        subscriber.Upsert(mutation.Side, mutation.Level.Price, mutation.Level.Quantity);
                }

                AssertSameBook(publisher.Book, subscriber, round, Mutation.None);
            }
        }

        /// <summary>
        /// The book must never cross: no bid at or above the best ask. A crossed book is not a
        /// state a matching engine can be in, so emitting one means the feed is lying.
        /// </summary>
        [Theory]
        [MemberData(nameof(Implementations))]
        public void BookNeverCrosses(string implementation)
        {
            Property.ForAll(
                generate: random => random.Next(1, 5000),
                shrink: updates => updates > 1 ? new[] { updates / 2, updates - 1 } : Array.Empty<int>(),
                describe: updates => $"{updates} update(s)",
                cases: 40,
                property: updates =>
                {
                    var simulator = Create(implementation);
                    var random = new Random(4321);

                    for (var i = 0; i < updates; i++)
                    {
                        if (i % 97 == 0)
                            simulator.Refresh(random);
                        else
                            simulator.Mutate(random);

                        var spread = simulator.Book.Spread();

                        Assert.True(spread is null or > 0,
                            $"book crossed after update {i}: spread {spread}");
                    }
                });
        }

        /// <summary>
        /// Prices must stay inside the configured band. The ladder enforces this by throwing, so
        /// this property is what proves the other implementations are held to the same contract
        /// and remain interchangeable.
        /// </summary>
        [Theory]
        [MemberData(nameof(Implementations))]
        public void PricesStayWithinTheConfiguredBand(string implementation)
        {
            var simulator = Create(implementation);
            var random = new Random(7);

            for (var i = 0; i < 20_000; i++)
            {
                simulator.Mutate(random);

                foreach (var side in new[] { Side.Bid, Side.Ask })
                {
                    foreach (var level in simulator.ReadSide(side))
                    {
                        Assert.InRange(level.Price, -PriceBand, PriceBand);
                        Assert.True(level.Quantity > 0, "a displayed level must carry quantity");
                    }
                }
            }
        }

        /// <summary>
        /// Same seed, same stream - the guarantee that makes a failing run reproducible.
        /// </summary>
        [Fact]
        public void SimulationIsDeterministicForAGivenSeed()
        {
            static List<Mutation> RunOnce()
            {
                var simulator = new BookSimulator(BookFactory.Create("SortedArray", Depth, PriceBand), PriceBand);
                var random = new Random(20260819);
                var mutations = new List<Mutation>();

                for (var i = 0; i < 5000; i++)
                    mutations.Add(simulator.Mutate(random));

                return mutations;
            }

            Assert.Equal(RunOnce(), RunOnce());
        }

        private static void AssertSameBook(IOrderBook expected, IOrderBook actual, int step, Mutation mutation)
        {
            foreach (var side in new[] { Side.Bid, Side.Ask })
            {
                var expectedLevels = expected.ToList(side);
                var actualLevels = actual.ToList(side);

                Assert.True(expectedLevels.SequenceEqual(actualLevels),
                    $"subscriber diverged from publisher on {side} at step {step} after {mutation.Kind} " +
                    $"{mutation.Level.Price}@{mutation.Level.Quantity}\n" +
                    $"  publisher:  [{string.Join(", ", expectedLevels)}]\n" +
                    $"  subscriber: [{string.Join(", ", actualLevels)}]");
            }
        }
    }
}
