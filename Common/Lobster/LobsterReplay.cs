using MarketData.Common.Books;
using System;

namespace MarketData.Common.Lobster
{
    /// <summary>Outcome of replaying a message file against its reference book.</summary>
    public sealed class ReplayResult
    {
        public long MessagesApplied;
        public long RowsCompared;
        public long RowsMatched;

        /// <summary>Rows whose top-k levels matched, indexed by k-1 for k in 1..10.</summary>
        public readonly long[] MatchedByDepth = new long[LobsterReplay.LevelsInReference];
        public long FirstMismatchRow = -1;
        public string FirstMismatchDetail;
        public long NegativeLevels;
        public long HiddenExecutions;
        public double ElapsedSeconds;

        /// <summary>Transitions skipped because a level-10 snapshot cannot determine the outcome.</summary>
        public long Unverifiable;

        public double MatchRate => RowsCompared == 0 ? 0 : RowsMatched / (double)RowsCompared;
        public double MessagesPerSecond => ElapsedSeconds <= 0 ? 0 : MessagesApplied / ElapsedSeconds;
    }

    /// <summary>
    /// Replays a real NASDAQ session through an order book and checks the result against the
    /// exchange's own published book, message by message.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the strongest correctness statement available to the project. The synthetic tests
    /// prove the books agree with each other and with a reference implementation; this proves they
    /// agree with what NASDAQ actually published, over a real trading day, at every one of a
    /// quarter of a million messages. A reconstruction that is subtly wrong - an off-by-one in a
    /// depth cap, a mishandled deletion, a level that fails to disappear when it empties - cannot
    /// survive that.
    /// </para>
    /// <para>
    /// One honest caveat, inherent to the data rather than the code. The message file begins at
    /// 09:30:00 and the book is already populated at that instant by orders resting from the
    /// opening cross, whose arrivals are therefore not in the file. The book is seeded from the
    /// reference's first row, but only its top ten levels are visible there, so anything resting
    /// deeper is unknown; where that hidden depth later surfaces into the top ten, a
    /// reconstruction from messages alone cannot know about it. Replay can be told to skip an
    /// opening warm-up, after which the book has largely turned over and been rebuilt from
    /// messages the file does contain.
    /// </para>
    /// </remarks>
    public static class LobsterReplay
    {
        public const int LevelsInReference = 10;

        /// <param name="messages">Raw bytes of the LOBSTER message file.</param>
        /// <param name="reference">Raw bytes of the matching orderbook file.</param>
        /// <param name="book">The book under test. Must be deep enough not to truncate.</param>
        /// <param name="warmupMessages">
        /// Messages applied before comparison begins, after which the book is re-seeded from the
        /// reference. Lets the opening state settle before correctness is judged.
        /// </param>
        public static ReplayResult Replay(ReadOnlySpan<byte> messages, ReadOnlySpan<byte> reference,
            IOrderBook book, int warmupMessages = 0)
        {
            var result = new ReplayResult();
            var started = System.Diagnostics.Stopwatch.GetTimestamp();

            var messageReader = new LobsterReader(messages);
            var referenceReader = new LobsterReader(reference);

            Span<int> referenceRow = stackalloc int[LevelsInReference * 4];
            Span<PriceLevel> asks = stackalloc PriceLevel[LevelsInReference];
            Span<PriceLevel> bids = stackalloc PriceLevel[LevelsInReference];

            var row = 0L;

            while (messageReader.TryReadMessage(out var message))
            {
                if (!referenceReader.TryReadBookRow(referenceRow, out var fields) || fields < referenceRow.Length)
                    break;

                row++;

                if (message.Type == LobsterEventType.HiddenExecution)
                    result.HiddenExecutions++;

                Apply(book, message, result);
                result.MessagesApplied++;

                if (row == warmupMessages)
                {
                    // Re-seed from the exchange's own state, then judge everything after it.
                    Seed(book, referenceRow);
                    continue;
                }

                if (row <= warmupMessages)
                    continue;

                var askCount = book.CopyTo(Side.Ask, asks);
                var bidCount = book.CopyTo(Side.Bid, bids);

                result.RowsCompared++;

                // How deep the reconstruction is correct on this row. Reported as a curve rather
                // than a single verdict, because the honest answer depends on depth: the seed
                // reveals only ten levels, so anything resting deeper is unknowable from the
                // message file and surfaces as error at the bottom of the window first.
                var correctDepth = CorrectDepth(referenceRow, asks, askCount, bids, bidCount, out var detail);

                for (var k = 0; k < correctDepth; k++)
                    result.MatchedByDepth[k]++;

                if (correctDepth == LevelsInReference)
                {
                    result.RowsMatched++;
                }
                else if (result.FirstMismatchRow < 0)
                {
                    result.FirstMismatchRow = row;
                    result.FirstMismatchDetail = $"row {row} after {message}: {detail}";
                }
            }

            result.ElapsedSeconds = (System.Diagnostics.Stopwatch.GetTimestamp() - started)
                / (double)System.Diagnostics.Stopwatch.Frequency;

            return result;
        }

        /// <summary>Applies one message as a signed change to the resting size at its price.</summary>
        private static void Apply(IOrderBook book, in LobsterMessage message, ReplayResult result)
        {
            if (!message.AffectsVisibleBook)
                return;

            var side = message.Side;
            book.TryGetQuantity(side, message.Price, out var current);

            var next = current + message.SizeDelta;

            if (next <= 0)
            {
                // A level at or below zero has left the book. Below zero means the delta referred
                // to size resting before the file began; counted rather than hidden, because it is
                // the measurable footprint of the unknown opening state.
                if (next < 0)
                    result.NegativeLevels++;

                book.Remove(side, message.Price);
                return;
            }

            book.Upsert(side, message.Price, (uint)next);
        }

        /// <summary>
        /// Levels compared in a single-step transition. One fewer than the reference publishes,
        /// because a message that removes a level promotes an unknown eleventh level into the
        /// window; leaving one slot of slack keeps every compared level knowable.
        /// </summary>
        public const int TransitionLevels = LevelsInReference - 1;

        /// <summary>
        /// Validates each message as a single transition between two published book states.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the question a feed handler actually has to answer: given the book as it stands,
        /// does applying the next message produce the book the exchange publishes next? Seeding
        /// from the reference before every message means error cannot accumulate, so each of the
        /// quarter of a million transitions is an independent test rather than one long trajectory
        /// whose later steps are meaningless once an early one drifts.
        /// </para>
        /// <para>
        /// A level-10 snapshot cannot determine every outcome. When a message touches a price
        /// deeper than the tenth level, its effect is invisible in the window, and the step is
        /// counted as unverifiable rather than scored - reporting those as failures would blame the
        /// reconstruction for a limitation of the data.
        /// </para>
        /// </remarks>
        public static ReplayResult ReplayTransitions(ReadOnlySpan<byte> messages, ReadOnlySpan<byte> reference,
            IOrderBook book)
        {
            var result = new ReplayResult();
            var started = System.Diagnostics.Stopwatch.GetTimestamp();

            var messageReader = new LobsterReader(messages);
            var referenceReader = new LobsterReader(reference);

            Span<int> previousRow = stackalloc int[LevelsInReference * 4];
            Span<int> currentRow = stackalloc int[LevelsInReference * 4];
            Span<PriceLevel> asks = stackalloc PriceLevel[LevelsInReference];
            Span<PriceLevel> bids = stackalloc PriceLevel[LevelsInReference];

            // The first message's prior state is not in the file, so start from the second.
            if (!messageReader.TryReadMessage(out _) || !referenceReader.TryReadBookRow(previousRow, out _))
                return result;

            var row = 1L;

            while (messageReader.TryReadMessage(out var message) &&
                   referenceReader.TryReadBookRow(currentRow, out var fields) && fields == currentRow.Length)
            {
                row++;
                result.MessagesApplied++;

                if (message.Type == LobsterEventType.HiddenExecution)
                    result.HiddenExecutions++;

                if (!IsVerifiable(previousRow, message))
                {
                    result.Unverifiable++;
                    currentRow.CopyTo(previousRow);
                    continue;
                }

                Seed(book, previousRow);
                Apply(book, message, result);

                var askCount = book.CopyTo(Side.Ask, asks);
                var bidCount = book.CopyTo(Side.Bid, bids);

                result.RowsCompared++;

                var correctDepth = CorrectDepth(currentRow, asks, askCount, bids, bidCount, out var detail);

                for (var k = 0; k < correctDepth && k < result.MatchedByDepth.Length; k++)
                    result.MatchedByDepth[k]++;

                if (correctDepth >= TransitionLevels)
                {
                    result.RowsMatched++;
                }
                else if (result.FirstMismatchRow < 0)
                {
                    result.FirstMismatchRow = row;
                    result.FirstMismatchDetail = $"row {row} after {message}: {detail}";
                }

                currentRow.CopyTo(previousRow);
            }

            result.ElapsedSeconds = (System.Diagnostics.Stopwatch.GetTimestamp() - started)
                / (double)System.Diagnostics.Stopwatch.Frequency;

            return result;
        }

        /// <summary>
        /// True when a level-10 snapshot is enough to predict the message's effect on the window.
        /// </summary>
        private static bool IsVerifiable(ReadOnlySpan<int> previousRow, in LobsterMessage message)
        {
            if (!message.AffectsVisibleBook)
                return true; // no change expected, which is itself worth checking

            // The message must land at or inside the deepest level published for its side,
            // otherwise the snapshot simply does not say what is there.
            var isBid = message.Side == Side.Bid;
            var deepestOffset = (LevelsInReference - 1) * 4 + (isBid ? 2 : 0);
            var deepestPrice = previousRow[deepestOffset];
            var deepestSize = previousRow[deepestOffset + 1];

            if (deepestSize <= 0)
                return true; // fewer than ten levels published, so the whole book is visible

            return isBid ? message.Price >= deepestPrice : message.Price <= deepestPrice;
        }

        /// <summary>Replaces book state with a reference row, discarding whatever was there.</summary>
        public static void Seed(IOrderBook book, ReadOnlySpan<int> referenceRow)
        {
            book.Clear();

            for (var level = 0; level < LevelsInReference; level++)
            {
                var offset = level * 4;
                var askPrice = referenceRow[offset];
                var askSize = referenceRow[offset + 1];
                var bidPrice = referenceRow[offset + 2];
                var bidSize = referenceRow[offset + 3];

                if (askSize > 0)
                    book.Upsert(Side.Ask, askPrice, (uint)askSize);

                if (bidSize > 0)
                    book.Upsert(Side.Bid, bidPrice, (uint)bidSize);
            }
        }

        /// <summary>Number of leading levels, on both sides, that match the reference exactly.</summary>
        private static int CorrectDepth(ReadOnlySpan<int> referenceRow,
            ReadOnlySpan<PriceLevel> asks, int askCount,
            ReadOnlySpan<PriceLevel> bids, int bidCount, out string detail)
        {
            for (var level = 0; level < LevelsInReference; level++)
            {
                var offset = level * 4;

                if (!LevelMatches(referenceRow[offset], referenceRow[offset + 1], asks, askCount, level, "ask", out detail) ||
                    !LevelMatches(referenceRow[offset + 2], referenceRow[offset + 3], bids, bidCount, level, "bid", out detail))
                {
                    return level;
                }
            }

            detail = null;
            return LevelsInReference;
        }

        private static bool LevelMatches(int expectedPrice, int expectedSize,
            ReadOnlySpan<PriceLevel> actual, int actualCount, int index, string side, out string detail)
        {
            var present = index < actualCount;
            var actualPrice = present ? actual[index].Price : 0;
            var actualSize = present ? (int)actual[index].Quantity : 0;

            if (actualPrice == expectedPrice && actualSize == expectedSize)
            {
                detail = null;
                return true;
            }

            detail = $"{side} level {index + 1}: expected {expectedSize}@{expectedPrice}, got {actualSize}@{actualPrice}";
            return false;
        }
    }
}
