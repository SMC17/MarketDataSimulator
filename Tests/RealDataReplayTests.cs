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

        public static TheoryData<string> Implementations() => new TheoryData<string>
        {
            "SortedArray", "Ladder", "Tree",
        };

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
        [MemberData(nameof(Implementations))]
        public void ReproducesNasdaqBookOnEveryTransition(string implementation)
        {
            var messages = Load("AMZN_message_20k.csv.gz");
            var reference = Load("AMZN_orderbook_20k.csv.gz");

            var book = implementation == "Ladder"
                ? new LadderBook(4096, 2_100_000, 2_400_000)
                : BookFactory.Create(implementation, 4096, 2_400_000);

            var result = LobsterReplay.ReplayTransitions(messages, reference, book);

            Assert.True(result.RowsCompared > 15_000,
                $"expected most of the slice to be verifiable, compared {result.RowsCompared}");

            Assert.True(result.RowsMatched == result.RowsCompared,
                $"{implementation} diverged from NASDAQ on {result.RowsCompared - result.RowsMatched} " +
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
