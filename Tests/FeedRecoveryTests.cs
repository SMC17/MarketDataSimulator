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
        private const ulong Session = 0xA11CE;

        private static FeedDecoder NewDecoder() =>
            new FeedDecoder(_ => BookFactory.Create("SortedArray", Depth, 512));

        private static byte[] Packet(ulong firstSequence,
            params (FeedMessageType Type, Side Side, int Price, uint Quantity)[] messages)
            => Packet(Session, Instrument, firstSequence, messages);

        private static byte[] Packet(ulong session, int instrument, ulong firstSequence,
            params (FeedMessageType Type, Side Side, int Price, uint Quantity)[] messages)
        {
            var buffer = new byte[FeedProtocol.MaxPacketSize];
            var offset = FeedProtocol.HeaderSize;

            foreach (var message in messages)
            {
                offset += FeedProtocol.WriteIncremental(buffer.AsSpan(offset), message.Type, instrument,
                    message.Side, new PriceLevel(message.Price, message.Quantity));
            }

            FeedProtocol.WriteHeader(buffer.AsSpan(0, offset), (ushort)messages.Length, session,
                firstSequence, 1234);
            return buffer.AsSpan(0, offset).ToArray();
        }

        private static byte[] SnapshotPacket(ulong firstSequence, PriceLevel[] bids, PriceLevel[] asks,
            int instrument = Instrument, ulong session = Session)
        {
            var buffer = new byte[FeedProtocol.MaxPacketSize];
            var offset = FeedProtocol.HeaderSize;
            offset += FeedProtocol.WriteSnapshot(buffer.AsSpan(offset), instrument, bids, asks);
            FeedProtocol.WriteHeader(buffer.AsSpan(0, offset), 1, session, firstSequence, 1234);
            return buffer.AsSpan(0, offset).ToArray();
        }

        private static byte[] EmptySnapshot(ulong sequence = 0, int instrument = Instrument,
            ulong session = Session) => SnapshotPacket(sequence, Array.Empty<PriceLevel>(),
                Array.Empty<PriceLevel>(), instrument, session);

        [Fact]
        public void CleanStreamAppliesEveryMessage()
        {
            var decoder = NewDecoder();

            decoder.Consume(EmptySnapshot());
            decoder.Consume(Packet(1, (FeedMessageType.Add, Side.Bid, -1, 100), (FeedMessageType.Add, Side.Ask, 1, 200)));
            decoder.Consume(Packet(3, (FeedMessageType.Add, Side.Bid, -2, 300)));

            Assert.False(decoder.IsStale);
            Assert.Equal(0, decoder.Statistics.Gaps);
            Assert.Equal(4, decoder.Statistics.Messages);
            Assert.Equal(2, decoder.BookFor(Instrument).Count(Side.Bid));
        }

        [Fact]
        public void AGapIsDetectedAndCountedExactly()
        {
            var decoder = NewDecoder();

            decoder.Consume(EmptySnapshot());
            decoder.Consume(Packet(1, (FeedMessageType.Add, Side.Bid, -1, 100)));
            // Sequences 2..6 never arrive.
            decoder.Consume(Packet(7, (FeedMessageType.Add, Side.Bid, -2, 100)));

            // Held, not yet declared lost: at this instant loss is indistinguishable from
            // reordering. The gap timer is what resolves it.
            Assert.False(decoder.IsStale);
            Assert.Equal(1, decoder.HeldPackets);

            decoder.FlushGaps();

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

            decoder.Consume(EmptySnapshot());
            decoder.Consume(Packet(1, (FeedMessageType.Add, Side.Bid, -1, 100)));
            decoder.Consume(Packet(9, (FeedMessageType.Add, Side.Bid, -2, 100)));
            decoder.FlushGaps();

            Assert.True(decoder.IsStale);
            // The held packet is applied on resumption, but nothing further is trusted.
            var afterGap = decoder.BookFor(Instrument).Count(Side.Bid);

            decoder.Consume(Packet(10, (FeedMessageType.Add, Side.Bid, -3, 100)));
            Assert.Equal(afterGap, decoder.BookFor(Instrument).Count(Side.Bid));
        }

        [Fact]
        public void ASnapshotClearsStalenessAndRestoresTheBook()
        {
            var decoder = NewDecoder();

            decoder.Consume(EmptySnapshot());
            decoder.Consume(Packet(1, (FeedMessageType.Add, Side.Bid, -1, 100)));
            decoder.Consume(Packet(50, (FeedMessageType.Add, Side.Bid, -2, 100)));
            decoder.FlushGaps();
            Assert.True(decoder.IsStale);

            decoder.Consume(SnapshotPacket(51,
                new[] { new PriceLevel(-5, 500), new PriceLevel(-6, 600) },
                new[] { new PriceLevel(5, 500) }));

            Assert.False(decoder.IsStale);
            Assert.Equal(2, decoder.Statistics.Recoveries);

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

            decoder.Consume(EmptySnapshot());
            var first = Packet(1, (FeedMessageType.Add, Side.Bid, -1, 100));
            decoder.Consume(first);
            decoder.Consume(Packet(2, (FeedMessageType.Replace, Side.Bid, -1, 999)));

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
            decoder.Consume(EmptySnapshot());
            decoder.Consume(Packet(1, (FeedMessageType.Add, Side.Bid, -1, 100)));

            // Claims more messages than the payload can hold.
            var truncated = Packet(2, (FeedMessageType.Add, Side.Bid, -2, 100));
            FeedProtocol.WriteHeader(truncated, 50, Session, 2, 1234);
            decoder.Consume(truncated);

            // Unknown message type.
            var unknown = Packet(2, (FeedMessageType.Add, Side.Bid, -3, 100));
            unknown[FeedProtocol.HeaderSize] = 0xEE;
            FeedProtocol.WriteHeader(unknown, 1, Session, 2, 1234);
            decoder.Consume(unknown);

            // Foreign traffic on the group.
            decoder.Consume(new byte[] { 1, 2, 3, 4, 5 });

            Assert.Equal(3, decoder.Statistics.Malformed);

            // None of them was allowed to touch the book...
            Assert.True(decoder.BookFor(Instrument).TryGetBest(Side.Bid, out var best));
            Assert.Equal(-1, best.Price);

            // ...nor to move the sequence on, so a valid packet still applies cleanly and the
            // consumer is not left permanently convinced it has missed something.
            decoder.Consume(Packet(2, (FeedMessageType.Add, Side.Bid, -2, 100)));

            Assert.False(decoder.IsStale);
            Assert.Equal(0, decoder.Statistics.Gaps);
            Assert.Equal(2, decoder.BookFor(Instrument).Count(Side.Bid));
        }

        [Fact]
        public void LateJoinerIgnoresIncrementalsUntilItsFirstSnapshot()
        {
            var decoder = NewDecoder();

            decoder.Consume(Packet(100, (FeedMessageType.Add, Side.Bid, -1, 100)));

            Assert.True(decoder.IsStale);
            Assert.True(decoder.IsInstrumentStale(Instrument));
            Assert.Equal(1, decoder.Statistics.IgnoredIncrementals);
            Assert.Equal(0, decoder.BookFor(Instrument).Count(Side.Bid));

            decoder.Consume(SnapshotPacket(101, new[] { new PriceLevel(-5, 500) },
                new[] { new PriceLevel(5, 500) }));
            decoder.Consume(Packet(102, (FeedMessageType.Add, Side.Bid, -6, 600)));

            Assert.False(decoder.IsStale);
            Assert.Equal(new[] { -5, -6 },
                decoder.BookFor(Instrument).ToList(Side.Bid).Select(level => level.Price));
        }

        [Fact]
        public void SnapshotRecoversOnlyItsInstrument()
        {
            const int secondInstrument = 2;
            var decoder = NewDecoder();

            decoder.Consume(EmptySnapshot(0, Instrument));
            decoder.Consume(EmptySnapshot(1, secondInstrument));
            decoder.Consume(Packet(Session, Instrument, 2,
                (FeedMessageType.Add, Side.Bid, -1, 100)));
            decoder.Consume(Packet(Session, secondInstrument, 3,
                (FeedMessageType.Add, Side.Ask, 1, 100)));

            decoder.Consume(Packet(Session, Instrument, 5,
                (FeedMessageType.Add, Side.Bid, -2, 100)));
            decoder.FlushGaps();
            decoder.Consume(EmptySnapshot(6, Instrument));

            Assert.False(decoder.IsInstrumentStale(Instrument));
            Assert.True(decoder.IsInstrumentStale(secondInstrument));
            Assert.True(decoder.IsStale);

            decoder.Consume(Packet(Session, secondInstrument, 7,
                (FeedMessageType.Add, Side.Ask, 2, 100)));
            Assert.Equal(1, decoder.BookFor(secondInstrument).Count(Side.Ask));
            Assert.Equal(2, decoder.Statistics.IgnoredIncrementals);
        }

        [Fact]
        public void PublisherRestartRequiresRecoveryAndRejectsLatePriorSessionPackets()
        {
            const ulong restartedSession = Session + 1;
            var decoder = NewDecoder();

            decoder.Consume(EmptySnapshot());
            decoder.Consume(Packet(1, (FeedMessageType.Add, Side.Bid, -1, 100)));

            decoder.Consume(Packet(restartedSession, Instrument, 0,
                (FeedMessageType.Add, Side.Bid, -2, 200)));

            Assert.True(decoder.IsStale);
            Assert.Equal(restartedSession, decoder.SessionId);
            Assert.Equal(1, decoder.Statistics.SessionChanges);

            decoder.Consume(EmptySnapshot(1, Instrument, restartedSession));
            decoder.Consume(Packet(restartedSession, Instrument, 2,
                (FeedMessageType.Add, Side.Bid, -3, 300)));
            decoder.Consume(Packet(Session, Instrument, 2,
                (FeedMessageType.Add, Side.Bid, -9, 900)));

            Assert.False(decoder.IsStale);
            Assert.Equal(new[] { -3 },
                decoder.BookFor(Instrument).ToList(Side.Bid).Select(level => level.Price));
            Assert.Equal(1, decoder.Statistics.OldSessionPackets);
        }

        [Fact]
        public void CorruptionDoesNotAdvanceSequenceOrMutateState()
        {
            var decoder = NewDecoder();
            decoder.Consume(EmptySnapshot());
            decoder.Consume(Packet(1, (FeedMessageType.Add, Side.Bid, -1, 100)));

            var corrupt = Packet(2, (FeedMessageType.Add, Side.Bid, -2, 200));
            corrupt[^1] ^= 1;
            decoder.Consume(corrupt);

            Assert.Equal(2UL, decoder.ExpectedSequence);
            Assert.Equal(1, decoder.Statistics.IntegrityFailures);
            Assert.Equal(new[] { -1 },
                decoder.BookFor(Instrument).ToList(Side.Bid).Select(level => level.Price));

            decoder.Consume(Packet(2, (FeedMessageType.Add, Side.Bid, -2, 200)));
            Assert.Equal(3UL, decoder.ExpectedSequence);
            Assert.Equal(2, decoder.BookFor(Instrument).Count(Side.Bid));
        }

        [Fact]
        public void ObserverFailureCannotInterruptFeedState()
        {
            var decoder = NewDecoder();
            decoder.MessageObserved += _ => throw new InvalidOperationException("observer failed");

            decoder.Consume(EmptySnapshot());
            decoder.Consume(Packet(1, (FeedMessageType.Add, Side.Bid, -1, 100)));

            Assert.Equal(2UL, decoder.ExpectedSequence);
            Assert.False(decoder.IsStale);
            Assert.Equal(1, decoder.BookFor(Instrument).Count(Side.Bid));
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

                    // Resolve anything still held before judging: unflushed holds are neither
                    // applied nor reported yet.
                    decoder.FlushGaps();

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
