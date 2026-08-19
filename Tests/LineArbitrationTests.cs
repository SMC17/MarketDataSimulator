using System;
using System.Linq;
using MarketData.Common.Books;
using MarketData.Common.Feed;
using MarketData.Tests.Framework;
using Xunit;

namespace MarketData.Tests
{
    /// <summary>
    /// A and B line redundancy, exercised at the decoder so loss rates can be dialled precisely.
    /// </summary>
    public class LineArbitrationTests
    {
        private const int Instrument = 1;
        private const ulong Session = 0xAB;

        private static FeedDecoder NewDecoder() =>
            new FeedDecoder(_ => BookFactory.Create("SortedArray", 10, 512));

        private static byte[] Packet(ulong sequence, int price)
        {
            var buffer = new byte[FeedProtocol.MaxPacketSize];
            var offset = FeedProtocol.HeaderSize;
            offset += FeedProtocol.WriteIncremental(buffer.AsSpan(offset), FeedMessageType.Add, Instrument,
                Side.Bid, new PriceLevel(price, 100));
            FeedProtocol.WriteHeader(buffer.AsSpan(0, offset), 1, Session, sequence, 0);
            return buffer.AsSpan(0, offset).ToArray();
        }

        private static byte[] EmptySnapshot()
        {
            var buffer = new byte[FeedProtocol.MaxPacketSize];
            var offset = FeedProtocol.HeaderSize;
            offset += FeedProtocol.WriteSnapshot(buffer.AsSpan(offset), Instrument,
                Array.Empty<PriceLevel>(), Array.Empty<PriceLevel>());
            FeedProtocol.WriteHeader(buffer.AsSpan(0, offset), 1, Session, 0, 0);
            return buffer.AsSpan(0, offset).ToArray();
        }

        [Fact]
        public void SecondCopyOfAPacketIsDiscarded()
        {
            var decoder = NewDecoder();
            decoder.Consume(EmptySnapshot());
            var packet = Packet(1, -1);

            decoder.Consume(packet); // A line
            decoder.Consume(packet); // B line, identical

            Assert.Equal(1, decoder.Statistics.Duplicates);
            Assert.Equal(0, decoder.Statistics.Gaps);
            Assert.Equal(1, decoder.BookFor(Instrument).Count(Side.Bid));
        }

        [Fact]
        public void ConflictingCopyAtTheSameSequenceIsReported()
        {
            var decoder = NewDecoder();
            decoder.Consume(EmptySnapshot());

            decoder.Consume(Packet(1, -1));
            decoder.Consume(Packet(1, -2));

            Assert.Equal(1, decoder.Statistics.Duplicates);
            Assert.Equal(1, decoder.Statistics.LineDivergences);
            Assert.Equal(new[] { -1 },
                decoder.BookFor(Instrument).ToList(Side.Bid).Select(level => level.Price));
        }

        [Fact]
        public void LossOnOneLineIsCoveredByTheOther()
        {
            var decoder = NewDecoder();
            decoder.Consume(EmptySnapshot());

            // A delivers 1, drops 2. B drops 1, delivers 2.
            decoder.Consume(Packet(1, -1));
            decoder.Consume(Packet(2, -2));

            Assert.Equal(0, decoder.Statistics.Gaps);
            Assert.False(decoder.IsStale);
            Assert.Equal(2, decoder.BookFor(Instrument).Count(Side.Bid));
        }

        /// <summary>
        /// The reason exchanges pay for a second line: with independent drops, a gap requires the
        /// same packet to be lost on both, so the gap rate falls roughly as the square of the
        /// per-line loss rate.
        /// </summary>
        [Fact]
        public void RedundancyReducesGapsFarBelowSingleLineLoss()
        {
            Property.ForAll(
                generate: random => (Seed: random.Next(), LossPercent: random.Next(5, 30)),
                shrink: _ => Array.Empty<(int, int)>(),
                describe: c => $"seed {c.Seed}, {c.LossPercent}% per-line loss",
                cases: 25,
                property: c =>
                {
                    const int packets = 4000;

                    var single = NewDecoder();
                    var redundant = NewDecoder();
                    var random = new Random(c.Seed);
                    single.Consume(EmptySnapshot());
                    redundant.Consume(EmptySnapshot());

                    for (var i = 0; i < packets; i++)
                    {
                        var packet = Packet((ulong)i + 1, -(i % 9) - 1);

                        var aDelivered = random.Next(100) >= c.LossPercent;
                        var bDelivered = random.Next(100) >= c.LossPercent;

                        if (aDelivered)
                            single.Consume(packet);

                        // Both lines feed one decoder; whichever arrives first wins.
                        if (aDelivered)
                            redundant.Consume(packet);

                        if (bDelivered)
                            redundant.Consume(packet);
                    }

                    Assert.True(redundant.Statistics.Gaps < single.Statistics.Gaps,
                        $"redundant feed should gap less: {redundant.Statistics.Gaps} vs {single.Statistics.Gaps}");

                    // Independent drops, so a gap needs both lines to lose the same packet.
                    var expectedRatio = c.LossPercent / 100.0;
                    var observedRatio = redundant.Statistics.Gaps / (double)Math.Max(1, single.Statistics.Gaps);

                    Assert.True(observedRatio < expectedRatio * 2.5,
                        $"expected roughly {expectedRatio:P0} of the single-line gaps, saw {observedRatio:P0} " +
                        $"({redundant.Statistics.Gaps} vs {single.Statistics.Gaps})");
                });
        }

        [Fact]
        public void ArbitrationSurvivesArbitraryInterleavingOfTheTwoLines()
        {
            Property.ForAll(
                generate: random => random.Next(),
                shrink: _ => Array.Empty<int>(),
                describe: seed => $"seed {seed}",
                cases: 50,
                property: seed =>
                {
                    var random = new Random(seed);
                    var decoder = NewDecoder();
                    var reference = BookFactory.Create("SortedArray", 10, 512);
                    decoder.Consume(EmptySnapshot());

                    var pending = new System.Collections.Generic.List<byte[]>();

                    // Reordering is bounded, as it is in reality: the A and B lines differ by a
                    // path delay, not by an unbounded amount. A bounded hold buffer cannot absorb
                    // unbounded reordering, and the test below pins down what happens when the
                    // bound is exceeded.
                    const int maxInFlight = 16;

                    for (var i = 0; i < 500; i++)
                    {
                        var price = -(i % 7) - 1;
                        var packet = Packet((ulong)i + 1, price);
                        reference.Upsert(Side.Bid, price, 100);

                        // Both copies enter a queue drained in random order, modelling the two
                        // lines arriving with arbitrary relative delay.
                        pending.Add(packet);
                        pending.Add(packet);

                        while (pending.Count > maxInFlight || (pending.Count > 0 && random.Next(3) > 0))
                        {
                            var index = random.Next(pending.Count);
                            decoder.Consume(pending[index]);
                            pending.RemoveAt(index);
                        }
                    }

                    foreach (var packet in pending)
                        decoder.Consume(packet);

                    Assert.False(decoder.IsStale);
                    Assert.Equal(0, decoder.Statistics.Gaps);
                    Assert.True(decoder.Statistics.Reordered > 0, "the interleaving should have exercised the hold buffer");
                    Assert.True(reference.ToList(Side.Bid).SequenceEqual(decoder.BookFor(Instrument).ToList(Side.Bid)));
                });
        }

        /// <summary>
        /// Reordering deeper than the hold buffer is indistinguishable from loss and is reported
        /// as such. Documents the limit of the tolerance rather than pretending it is unbounded.
        /// </summary>
        [Fact]
        public void ReorderingBeyondTheHoldBufferIsReportedAsLoss()
        {
            var decoder = NewDecoder();
            decoder.Consume(EmptySnapshot());
            decoder.Consume(Packet(1, -1));

            // Everything from sequence 2 arrives except sequence 2 itself.
            for (var sequence = 3UL; sequence <= FeedDecoder.MaxHeldPackets + 3; sequence++)
                decoder.Consume(Packet(sequence, -2));

            Assert.True(decoder.IsStale);
            Assert.Equal(1, decoder.Statistics.Gaps);
            Assert.Equal(1, decoder.Statistics.MissedMessages);
        }
    }
}
