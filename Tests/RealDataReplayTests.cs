using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using MarketData.Common.Books;
using MarketData.Common.Lobster;
using Xunit;

namespace MarketData.Tests
{
    /// <summary>
    /// The order books against real NASDAQ market data.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every other test in this suite compares the implementation against something else this
    /// repository wrote - another book, a naive reference, an invariant chosen by the author. This
    /// one compares it against what NASDAQ actually published: a real AMZN session from
    /// 2012-06-21, its order events and the exchange's own resulting order book, message by
    /// message.
    /// </para>
    /// <para>
    /// The data is LOBSTER's reconstruction of NASDAQ's ITCH feed. A 20,000-message slice is
    /// committed so this runs offline; the full 269,748-message session is fetched by
    /// scripts/fetch-lobster.sh and driven by the replay benchmark.
    /// </para>
    /// </remarks>
    public class RealDataReplayTests
    {
        private static byte[] Load(string name)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "data", name);

            Assert.True(File.Exists(path), $"missing sample data: {path}");

            using var file = File.OpenRead(path);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var buffer = new MemoryStream();

            gzip.CopyTo(buffer);
            return buffer.ToArray();
        }

        public static TheoryData<string, string, int> Cases()
        {
            var data = new TheoryData<string, string, int>();

            // Two instruments with deliberately different microstructure and different reference
            // depths: AMZN at $223 publishes ten levels, MSFT at $30 publishes five and sits
            // pinned near a one-tick spread. The level count is inferred from the file, so this
            // also covers that inference.
            foreach (var implementation in new[] { "SortedArray", "Vectorized", "Ladder", "Tree" })
            {
                data.Add(implementation, "AMZN", 10);
                data.Add(implementation, "MSFT", 5);
            }

            return data;
        }

        [Fact]
        public void DiscoversCompressedSamplePairs()
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "data");
            var sessions = LobsterSessions.Discover(directory);

            Assert.Equal(new[] { "AMZN", "MSFT" }, sessions.Select(session => session.Symbol));
            Assert.Equal(new[] { 10, 5 }, sessions.Select(session => session.Levels));
        }

        /// <summary>
        /// Given the book as the exchange published it, applying the next real message must
        /// produce the book the exchange published next - for every message in the session.
        /// </summary>
        /// <remarks>
        /// Seeding from the reference before each message keeps the transitions independent, so
        /// this is a quarter of a million separate assertions rather than one trajectory whose
        /// later steps stop meaning anything after an early divergence. It is also the question a
        /// production feed handler has to get right on every single update.
        /// </remarks>
        [Theory]
        [MemberData(nameof(Cases))]
        public void ReproducesNasdaqBookOnEveryTransition(string implementation, string symbol, int expectedLevels)
        {
            var messages = Load($"{symbol}_message_20k.csv.gz");
            var reference = Load($"{symbol}_orderbook_20k.csv.gz");

            Assert.Equal(expectedLevels, LobsterReplay.DetectLevels(reference));

            // Bands are per instrument: MSFT traded near $30 and AMZN near $223, and a ladder
            // sized for one cannot represent the other.
            var (low, high) = symbol == "MSFT" ? (250_000, 400_000) : (2_100_000, 2_400_000);

            var book = implementation == "Ladder"
                ? new LadderBook(4096, low, high)
                : BookFactory.Create(implementation, 4096, high);

            var result = LobsterReplay.ReplayTransitions(messages, reference, book);

            Assert.True(result.RowsCompared > 15_000,
                $"expected most of the slice to be verifiable, compared {result.RowsCompared}");

            Assert.True(result.RowsMatched == result.RowsCompared,
                $"{implementation} diverged from NASDAQ on {symbol} in {result.RowsCompared - result.RowsMatched} " +
                $"of {result.RowsCompared} transitions. First: {result.FirstMismatchDetail}");
        }

        /// <summary>
        /// Hidden executions are invisible by design and must leave the displayed book untouched.
        /// A reconstruction that reacts to them drifts in a way that is very hard to trace back.
        /// </summary>
        [Fact]
        public void HiddenExecutionsDoNotMoveTheVisibleBook()
        {
            var messages = Load("AMZN_message_20k.csv.gz");
            var reader = new LobsterReader(messages);
            var hidden = 0;

            while (reader.TryReadMessage(out var message))
            {
                if (message.Type != LobsterEventType.HiddenExecution)
                    continue;

                hidden++;
                Assert.False(message.AffectsVisibleBook);
                Assert.Equal(0, message.SizeDelta);
            }

            Assert.True(hidden > 0, "the slice should contain hidden executions to exercise this");
        }

        [Fact]
        public void CumulativeReplaySeedsFromTheFirstPublishedRow()
        {
            var messages = Load("AMZN_message_20k.csv.gz");
            var reference = Load("AMZN_orderbook_20k.csv.gz");
            var book = new SortedArrayBook(4096);

            var result = LobsterReplay.Replay(messages, reference, book);

            Assert.Equal(19_999, result.RowsCompared);
            Assert.True(result.FirstMismatchRow != 1,
                $"replay compared before its first seed: {result.FirstMismatchDetail}");
            Assert.True(result.RowsMatched > 0 && result.MatchedByDepth[0] > 5_000,
                $"expected meaningful cumulative agreement, got {result.RowsMatched} full rows and " +
                $"{result.MatchedByDepth[0]} correct touches");
        }

        [Fact]
        public void LevelOneTransitionCannotPassWithoutMatchingItsOnlyLevel()
        {
            var messages = System.Text.Encoding.ASCII.GetBytes(
                "1.0,1,1,100,1000,1\n2.0,2,1,10,1000,1\n");
            var incorrectReference = System.Text.Encoding.ASCII.GetBytes(
                "1100,100,1000,100\n1100,100,1000,91\n");

            var result = LobsterReplay.ReplayTransitions(messages, incorrectReference,
                new SortedArrayBook(8));

            Assert.Equal(1, result.RowsCompared);
            Assert.Equal(0, result.RowsMatched);
            Assert.Equal(2, result.FirstMismatchRow);
        }

        [Fact]
        public void LevelOneDeletionIsMarkedUnverifiable()
        {
            var messages = System.Text.Encoding.ASCII.GetBytes(
                "1.0,1,1,100,1000,1\n2.0,3,1,100,1000,1\n");
            var reference = System.Text.Encoding.ASCII.GetBytes(
                "1100,100,1000,100\n1100,100,900,100\n");

            var result = LobsterReplay.ReplayTransitions(messages, reference,
                new SortedArrayBook(8));

            Assert.Equal(0, result.RowsCompared);
            Assert.Equal(0, result.RowsMatched);
            Assert.Equal(1, result.Unverifiable);
        }

        /// <summary>
        /// The parser must not allocate: it runs once per message, and a session is millions of
        /// them.
        /// </summary>
        [Fact]
        public void ParsingRealDataAllocatesNothing()
        {
            var messages = Load("AMZN_message_20k.csv.gz");

            // Warm up so JIT and first-call costs are not attributed to the measured pass.
            var warm = new LobsterReader(messages);
            while (warm.TryReadMessage(out _)) { }

            var before = GC.GetAllocatedBytesForCurrentThread();

            var reader = new LobsterReader(messages);
            var count = 0;
            long checksum = 0;

            while (reader.TryReadMessage(out var message))
            {
                checksum += message.Price;
                count++;
            }

            var bytes = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.True(count > 19_000, $"expected the whole slice, parsed {count}");
            Assert.NotEqual(0, checksum);
            Assert.True(bytes == 0, $"expected zero allocation parsing {count} messages, measured {bytes} bytes");
        }

        /// <summary>
        /// Timestamps carry nanosecond precision and must survive parsing exactly - a double
        /// cannot hold 34200.123456789 and arithmetic on it would silently reorder events.
        /// </summary>
        [Fact]
        public void TimestampsParseToExactNanoseconds()
        {
            var reader = new LobsterReader(System.Text.Encoding.ASCII.GetBytes(
                "34200.017459617,5,0,1,2238200,-1\n34200.189607670,1,11885113,21,2238100,1\n"));

            Assert.True(reader.TryReadMessage(out var first));
            Assert.Equal(34_200_017_459_617L, first.TimeNanoseconds);
            Assert.Equal(LobsterEventType.HiddenExecution, first.Type);
            Assert.Equal(2_238_200, first.Price);
            Assert.Equal(Side.Ask, first.Side);

            Assert.True(reader.TryReadMessage(out var second));
            Assert.Equal(34_200_189_607_670L, second.TimeNanoseconds);
            Assert.Equal(11_885_113, second.OrderId);
            Assert.Equal(21u, second.Size);
            Assert.Equal(Side.Bid, second.Side);
        }
    }
}
