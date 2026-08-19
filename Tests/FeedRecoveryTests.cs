using System;
using System.Collections.Generic;
using System.Linq;
using MarketData.Common.Books;
using MarketData.Common.Feed;
using MarketData.Tests.Framework;
using Xunit;

namespace MarketData.Tests
{
    /// <summary>
    /// Loss, reordering and recovery on an unreliable transport.
    /// </summary>
    /// <remarks>
    /// These are the paths that are almost impossible to provoke deliberately over a real network
    /// and catastrophic when wrong, so they are driven here by handing the decoder exactly the
    /// packet sequence of interest with no timing involved.
    /// </remarks>
    public class FeedRecoveryTests
    {
        private const int Depth = 10;
        private const int Instrument = 1;

        private static FeedDecoder NewDecoder() =>
            new FeedDecoder(_ => BookFactory.Create("SortedArray", Depth, 512));

        private static byte[] Packet(ulong firstSequence, params (FeedMessageType Type, Side Side, int Price, uint Quantity)[] messages)
        {
            var buffer = new byte[FeedProtocol.MaxPacketSize];
            var offset = FeedProtocol.HeaderSize;

            foreach (var message in messages)
            {
                offset += FeedProtocol.WriteIncremental(buffer.AsSpan(offset), message.Type, Instrument,
                    message.Side, new PriceLevel(message.Price, message.Quantity));
            }

            FeedProtocol.WriteHeader(buffer, (ushort)messages.Length, firstSequence, 1234);
            return buffer.AsSpan(0, offset).ToArray();
        }

        private static byte[] SnapshotPacket(ulong firstSequence, PriceLevel[] bids, PriceLevel[] asks)
        {
            var buffer = new byte[FeedProtocol.MaxPacketSize];
            var offset = FeedProtocol.HeaderSize;
            offset += FeedProtocol.WriteSnapshot(buffer.AsSpan(offset), Instrument, bids, asks);
            FeedProtocol.WriteHeader(buffer, 1, firstSequence, 1234);
            return buffer.AsSpan(0, offset).ToArray();
        }

        [Fact]
        public void CleanStreamAppliesEveryMessage()
        {
            var decoder = NewDecoder();

            decoder.Consume(Packet(0, (FeedMessageType.Add, Side.Bid, -1, 100), (FeedMessageType.Add, Side.Ask, 1, 200)));
            decoder.Consume(Packet(2, (FeedMessageType.Add, Side.Bid, -2, 300)));

            Assert.False(decoder.IsStale);
            Assert.Equal(0, decoder.Statistics.Gaps);
            Assert.Equal(3, decoder.Statistics.Messages);
            Assert.Equal(2, decoder.BookFor(Instrument).Count(Side.Bid));
        }

        [Fact]
        public void AGapIsDetectedAndCountedExactly()
        {
            var decoder = NewDecoder();

            decoder.Consume(Packet(0, (FeedMessageType.Add, Side.Bid, -1, 100)));
            // Sequences 1..5 never arrive.
            decoder.Consume(Packet(6, (FeedMessageType.Add, Side.Bid, -2, 100)));

            Assert.True(decoder.IsStale);
            Assert.Equal(1, decoder.Statistics.Gaps);
            Assert.Equal(5, decoder.Statistics.MissedMessages);
        }

        /// <summary>
        /// The central safety property: once a gap is seen, incrementals must not be applied,
        /// because a book built across a gap is wrong and gives no sign of it.
        /// </summary>
        [Fact]
        public void IncrementalsAreIgnoredWhileStale()
        {
            var decoder = NewDecoder();

            decoder.Consume(Packet(0, (FeedMessageType.Add, Side.Bid, -1, 100)));
            decoder.Consume(Packet(9, (FeedMessageType.Add, Side.Bid, -2, 100)));

            Assert.True(decoder.IsStale);
            Assert.Equal(1, decoder.BookFor(Instrument).Count(Side.Bid)); // the -2 level was not applied

            decoder.Consume(Packet(10, (FeedMessageType.Add, Side.Bid, -3, 100)));
            Assert.Equal(1, decoder.BookFor(Instrument).Count(Side.Bid));
        }

        [Fact]
        public void ASnapshotClearsStalenessAndRestoresTheBook()
        {
            var decoder = NewDecoder();

            decoder.Consume(Packet(0, (FeedMessageType.Add, Side.Bid, -1, 100)));
            decoder.Consume(Packet(50, (FeedMessageType.Add, Side.Bid, -2, 100)));
            Assert.True(decoder.IsStale);

            decoder.Consume(SnapshotPacket(51,
                new[] { new PriceLevel(-5, 500), new PriceLevel(-6, 600) },
                new[] { new PriceLevel(5, 500) }));

            Assert.False(decoder.IsStale);
            Assert.Equal(1, decoder.Statistics.Recoveries);

            var book = decoder.BookFor(Instrument);
            Assert.Equal(new[] { -5, -6 }, book.ToList(Side.Bid).Select(i => i.Price));
            Assert.Equal(new[] { 5 }, book.ToList(Side.Ask).Select(i => i.Price));

            // And incrementals flow again.
            decoder.Consume(Packet(52, (FeedMessageType.Add, Side.Bid, -7, 700)));
            Assert.Equal(3, book.Count(Side.Bid));
        }

        [Fact]
        public void DuplicateAndReorderedPacketsAreDiscarded()
        {
            var decoder = NewDecoder();

            var first = Packet(0, (FeedMessageType.Add, Side.Bid, -1, 100));
            decoder.Consume(first);
            decoder.Consume(Packet(1, (FeedMessageType.Replace, Side.Bid, -1, 999)));

            // Replaying the first packet must not undo the replace.
            decoder.Consume(first);

            Assert.Equal(1, decoder.Statistics.Duplicates);
            Assert.Equal(0, decoder.Statistics.Gaps);
            Assert.False(decoder.IsStale);
            Assert.True(decoder.BookFor(Instrument).TryGetBest(Side.Bid, out var best));
            Assert.Equal(999u, best.Quantity);
        }

        [Fact]
        public void MalformedPacketsAreRejectedWithoutCorruptingState()
        {
            var decoder = NewDecoder();
            decoder.Consume(Packet(0, (FeedMessageType.Add, Side.Bid, -1, 100)));

            // Claims more messages than the payload can hold.
            var truncated = Packet(1, (FeedMessageType.Add, Side.Bid, -2, 100));
            truncated[2] = 50;
            decoder.Consume(truncated);

            // Unknown message type.
            var unknown = Packet(1, (FeedMessageType.Add, Side.Bid, -3, 100));
            unknown[FeedProtocol.HeaderSize] = 0xEE;
            decoder.Consume(unknown);

            // Foreign traffic on the group.
            decoder.Consume(new byte[] { 1, 2, 3, 4, 5 });

            Assert.Equal(3, decoder.Statistics.Malformed);

            // None of them was allowed to touch the book...
            Assert.True(decoder.BookFor(Instrument).TryGetBest(Side.Bid, out var best));
            Assert.Equal(-1, best.Price);

            // ...nor to move the sequence on, so a valid packet still applies cleanly and the
            // consumer is not left permanently convinced it has missed something.
            decoder.Consume(Packet(1, (FeedMessageType.Add, Side.Bid, -2, 100)));

            Assert.False(decoder.IsStale);
            Assert.Equal(0, decoder.Statistics.Gaps);
            Assert.Equal(2, decoder.BookFor(Instrument).Count(Side.Bid));
        }

        /// <summary>
        /// Under random loss, a subscriber must end up either in step with the publisher or
        /// explicitly stale - never silently divergent. "Wrong but confident" is the outcome the
        /// whole staleness mechanism exists to rule out.
        /// </summary>
        [Fact]
        public void UnderRandomLossASubscriberIsEitherCorrectOrKnowsItIsNot()
        {
            Property.ForAll(
                generate: random => (Seed: random.Next(), LossPercent: random.Next(0, 40)),
                shrink: c => c.LossPercent > 0 ? new[] { (c.Seed, c.LossPercent / 2) } : Array.Empty<(int, int)>(),
                describe: c => $"seed {c.Seed}, {c.LossPercent}% loss",
                cases: 60,
                property: c =>
                {
                    var random = new Random(c.Seed);
                    var publisherBook = BookFactory.Create("SortedArray", Depth, 512);
                    var simulator = new BookSimulator(publisherBook, 512);
                    var decoder = NewDecoder();

                    ulong sequence = 0;

                    for (var i = 0; i < 600; i++)
                    {
                        byte[] packet;

                        if (i % 50 == 49)
                        {
                            packet = SnapshotPacket(sequence,
                                simulator.ReadSide(Side.Bid).ToArray(),
                                simulator.ReadSide(Side.Ask).ToArray());
                            sequence += 1;
                        }
                        else
                        {
                            var mutation = simulator.Mutate(random);

                            if (mutation.Kind == MutationKind.None)
                                continue;

                            var type = mutation.Kind switch
                            {
                                MutationKind.Add => FeedMessageType.Add,
                                MutationKind.Replace => FeedMessageType.Replace,
                                _ => FeedMessageType.Remove,
                            };

                            packet = Packet(sequence, (type, mutation.Side, mutation.Level.Price, mutation.Level.Quantity));
                            sequence += 1;
                        }

                        // The network drops this packet.
                        if (random.Next(100) < c.LossPercent)
                            continue;

                        decoder.Consume(packet);
                    }

                    if (decoder.IsStale)
                        return; // knows it cannot be trusted, which is the acceptable outcome

                    var subscriberBook = decoder.BookFor(Instrument);

                    foreach (var side in new[] { Side.Bid, Side.Ask })
                    {
                        Assert.True(publisherBook.ToList(side).SequenceEqual(subscriberBook.ToList(side)),
                            $"subscriber reported itself in sync but diverged on {side}:\n" +
                            $"  publisher:  [{string.Join(", ", publisherBook.ToList(side))}]\n" +
                            $"  subscriber: [{string.Join(", ", subscriberBook.ToList(side))}]");
                    }
                });
        }
    }
}
