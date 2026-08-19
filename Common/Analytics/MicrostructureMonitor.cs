using System.Runtime.CompilerServices;

namespace MarketData.Common.Analytics
{
    /// <summary>
    /// Top-of-book state at one instant, in integer price units.
    /// </summary>
    public readonly record struct Quote(int BidPrice, uint BidSize, int AskPrice, uint AskSize)
    {
        public bool IsTwoSided => BidSize > 0 && AskSize > 0;

        /// <summary>Midpoint, in half-ticks so it stays an exact integer.</summary>
        public long MidHalfTicks => (long)BidPrice + AskPrice;

        public int Spread => AskPrice - BidPrice;

        /// <summary>
        /// Size-weighted mid: the touch price weighted toward the thinner side.
        /// </summary>
        /// <remarks>
        /// Weighted by the <em>opposite</em> side's size, which is the direction that looks wrong
        /// until you think about queues. A large bid and a thin ask means the next trade is far
        /// more likely to lift the ask than to hit the bid, so fair value sits nearer the ask.
        /// Microprice is a better one-step predictor of the future mid than the mid itself.
        /// </remarks>
        public double Microprice
        {
            get
            {
                var total = (double)BidSize + AskSize;
                return total <= 0 ? 0 : (AskPrice * (double)BidSize + BidPrice * (double)AskSize) / total;
            }
        }

        /// <summary>Queue imbalance in [-1, 1]: +1 all size on the bid, -1 all on the ask.</summary>
        public double Imbalance
        {
            get
            {
                var total = (double)BidSize + AskSize;
                return total <= 0 ? 0 : ((double)BidSize - AskSize) / total;
            }
        }
    }

    /// <summary>
    /// Streaming microstructure statistics over a sequence of top-of-book quotes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything here is incremental and O(1) per update, holds no history, and allocates
    /// nothing. That is not gratuitous: a signal computed from the book has to be produced on the
    /// same path that consumes the book, at feed rate, and anything that allocates or rescans a
    /// window there is a signal that arrives too late to act on.
    /// </para>
    /// <para>
    /// The headline quantity is order flow imbalance, in the form given by Cont, Kukanov and
    /// Stoikov (2014). It measures net pressure at the touch by attributing each change in the
    /// best quotes to buying or selling interest: size added at the bid and size removed from the
    /// ask are both buying pressure, and vice versa. It is deliberately blind to trades as such -
    /// a cancelled bid and a bid lifted by a seller move the book identically, and the point is
    /// what the book did, not why.
    /// </para>
    /// </remarks>
    public sealed class MicrostructureMonitor
    {
        public bool HasQuote { get; private set; }
        public Quote Current { get; private set; }
        public Quote Previous { get; private set; }

        /// <summary>Order flow imbalance accumulated since the last <see cref="ResetFlow"/>.</summary>
        public long OrderFlowImbalance { get; private set; }

        /// <summary>Quotes seen since construction.</summary>
        public long Updates { get; private set; }

        /// <summary>Updates that moved the best bid or the best ask.</summary>
        public long TouchChanges { get; private set; }

        /// <summary>
        /// Feeds one top-of-book observation.
        /// </summary>
        /// <returns>The contribution this update made to order flow imbalance.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long Update(in Quote quote)
        {
            Updates++;

            if (!HasQuote)
            {
                HasQuote = true;
                Previous = quote;
                Current = quote;
                return 0;
            }

            Previous = Current;
            Current = quote;

            var contribution = Contribution(Previous, quote);
            OrderFlowImbalance += contribution;

            if (quote.BidPrice != Previous.BidPrice || quote.AskPrice != Previous.AskPrice)
                TouchChanges++;

            return contribution;
        }

        /// <summary>
        /// One update's order flow imbalance, per Cont-Kukanov-Stoikov.
        /// </summary>
        /// <remarks>
        /// Read each term as a question about the bid. If it rose, the whole new queue is fresh
        /// buying interest. If it fell, the whole old queue was buying interest that left. If it
        /// held, only the change in size counts. The ask contributes with the opposite sign, and
        /// the boundary cases overlap deliberately: when a price is unchanged both indicators fire
        /// and the terms subtract to exactly the size delta.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long Contribution(in Quote previous, in Quote current)
        {
            long flow = 0;

            if (current.BidPrice >= previous.BidPrice)
                flow += current.BidSize;

            if (current.BidPrice <= previous.BidPrice)
                flow -= previous.BidSize;

            if (current.AskPrice <= previous.AskPrice)
                flow -= current.AskSize;

            if (current.AskPrice >= previous.AskPrice)
                flow += previous.AskSize;

            return flow;
        }

        /// <summary>Clears the flow accumulator, keeping the quote so the next delta is continuous.</summary>
        public long ResetFlow()
        {
            var accumulated = OrderFlowImbalance;
            OrderFlowImbalance = 0;
            return accumulated;
        }
    }
}
