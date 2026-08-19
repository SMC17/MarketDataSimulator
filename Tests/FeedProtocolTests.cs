using System;
using System.Collections.Generic;
using System.Linq;
using MarketData.Common.Books;
using MarketData.Common.Feed;
using MarketData.Tests.Framework;
using Xunit;

namespace MarketData.Tests
{
    public class FeedProtocolTests
    {
        [Fact]
        public void HeaderRoundTrips()
        {
            var buffer = new byte[FeedProtocol.MaxPacketSize];
            FeedProtocol.WriteHeader(buffer, 7, 123456789012345UL, -98765L);

            Assert.True(FeedProtocol.TryReadHeader(buffer, out var count, out var sequence, out var timestamp));
            Assert.Equal(7, count);
            Assert.Equal(123456789012345UL, sequence);
            Assert.Equal(-98765L, timestamp);
        }

        [Fact]
        public void HeaderRejectsForeignBytes()
        {
            var buffer = new byte[FeedProtocol.MaxPacketSize];
            FeedProtocol.WriteHeader(buffer, 1, 1, 1);

            buffer[0] = 0xFF; // wrong magic
            Assert.False(FeedProtocol.TryReadHeader(buffer, out _, out _, out _));

            buffer[0] = FeedProtocol.Magic;
            buffer[1] = 99; // wrong version
            Assert.False(FeedProtocol.TryReadHeader(buffer, out _, out _, out _));

            Assert.False(FeedProtocol.TryReadHeader(new byte[3], out _, out _, out _));
        }

        [Fact]
        public void IncrementalRoundTripsOverArbitraryValues()
        {
            Property.ForAll(
                generate: random => (
                    Type: random.Next(2) == 0 ? FeedMessageType.Add : FeedMessageType.Remove,
                    Instrument: random.Next(int.MinValue, int.MaxValue),
                    Side: random.Next(2) == 0 ? Side.Bid : Side.Ask,
                    Price: random.Next(int.MinValue, int.MaxValue),
                    Quantity: (uint)random.Next()),
                shrink: _ => Array.Empty<(FeedMessageType, int, Side, int, uint)>(),
                describe: c => c.ToString(),
                property: c =>
                {
                    var buffer = new byte[FeedProtocol.IncrementalSize];
                    var written = FeedProtocol.WriteIncremental(buffer, c.Type, c.Instrument, c.Side,
                        new PriceLevel(c.Price, c.Quantity));

                    Assert.Equal(FeedProtocol.IncrementalSize, written);
                    Assert.Equal(written, FeedProtocol.MessageLength(buffer));

                    FeedProtocol.ReadIncremental(buffer, out var type, out var instrument, out var side, out var level);

                    Assert.Equal(c.Type, type);
                    Assert.Equal(c.Instrument, instrument);
                    Assert.Equal(c.Side, side);
                    Assert.Equal(c.Price, level.Price);
                    Assert.Equal(c.Quantity, level.Quantity);
                });
        }

        [Fact]
        public void SnapshotRoundTripsAndReportsItsLength()
        {
            Property.ForAll(
                generate: random =>
                {
                    var bids = Enumerable.Range(0, random.Next(0, 21))
                        .Select(i => new PriceLevel(-i, (uint)random.Next(1, 5000))).ToArray();
                    var asks = Enumerable.Range(0, random.Next(0, 21))
                        .Select(i => new PriceLevel(i + 1, (uint)random.Next(1, 5000))).ToArray();
                    return (Bids: bids, Asks: asks);
                },
                shrink: _ => Array.Empty<(PriceLevel[], PriceLevel[])>(),
                describe: c => $"{c.Bids.Length} bids, {c.Asks.Length} asks",
                property: c =>
                {
                    var buffer = new byte[FeedProtocol.MaxPacketSize];
                    var written = FeedProtocol.WriteSnapshot(buffer, 42, c.Bids, c.Asks);

                    Assert.Equal(FeedProtocol.SnapshotSize(c.Bids.Length, c.Asks.Length), written);
                    Assert.Equal(written, FeedProtocol.MessageLength(buffer));

                    var bids = new PriceLevel[64];
                    var asks = new PriceLevel[64];
                    FeedProtocol.ReadSnapshot(buffer, out var instrument, bids, out var bidCount, asks, out var askCount);

                    Assert.Equal(42, instrument);
                    Assert.Equal(c.Bids.Length, bidCount);
                    Assert.Equal(c.Asks.Length, askCount);
                    Assert.Equal(c.Bids, bids.Take(bidCount));
                    Assert.Equal(c.Asks, asks.Take(askCount));
                });
        }

        [Fact]
        public void MessageLengthRejectsUnknownTypes()
        {
            Assert.Equal(-1, FeedProtocol.MessageLength(new byte[] { 0 }));
            Assert.Equal(-1, FeedProtocol.MessageLength(new byte[] { 200 }));
            Assert.Equal(-1, FeedProtocol.MessageLength(ReadOnlySpan<byte>.Empty));
        }

        /// <summary>
        /// Batching must never build a datagram large enough to be fragmented: a fragmented
        /// packet is lost in its entirety if any one fragment is dropped.
        /// </summary>
        [Fact]
        public void BatchedPacketsStayUnderTheFragmentationThreshold()
        {
            var buffer = new byte[FeedProtocol.MaxPacketSize];
            var offset = FeedProtocol.HeaderSize;
            var messages = 0;

            while (offset + FeedProtocol.IncrementalSize <= FeedProtocol.MaxPacketSize)
            {
                offset += FeedProtocol.WriteIncremental(buffer.AsSpan(offset), FeedMessageType.Add, 1, Side.Bid,
                    new PriceLevel(messages, 100));
                messages++;
            }

            Assert.True(offset <= FeedProtocol.MaxPacketSize);
            Assert.True(FeedProtocol.MaxPacketSize <= 1472, "must fit a 1500-byte MTU with IP and UDP headers");
            Assert.True(messages > 90, $"expected a useful batch size, got {messages}");
        }
    }
}
