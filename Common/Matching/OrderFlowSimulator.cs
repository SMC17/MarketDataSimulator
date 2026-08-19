using MarketData.Common.Books;
using System;
using System.Collections.Generic;

namespace MarketData.Common.Matching
{
    /// <summary>
    /// Generates order flow and runs it through a matching engine.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This replaces the earlier random walk over aggregated price levels, and the difference is
    /// causal rather than cosmetic. Updates are now consequences of orders arriving, resting,
    /// cancelling and trading, so the published feed reflects a book that something actually did
    /// to it. A walk over aggregate levels can produce depth that no sequence of orders could
    /// create, which is precisely the kind of unreality that makes a simulator useless for testing
    /// anything downstream of it.
    /// </para>
    /// <para>
    /// The mix - mostly passive adds and cancels near the touch, occasionally something aggressive
    /// - is chosen to resemble a real venue's message profile, where cancels vastly outnumber
    /// trades. It is not calibrated to any particular market.
    /// </para>
    /// <para>
    /// Deterministic given a seed, and free of I/O and timers, so the properties that matter can
    /// be tested without a network.
    /// </para>
    /// </remarks>
    public sealed class OrderFlowSimulator
    {
        public LimitOrderBook Book { get; }

        /// <summary>Orders currently believed to be resting, used to pick cancellation targets.</summary>
        public int LiveOrders => _live.Count;

        public OrderFlowSimulator(LimitOrderBook book, int spreadWidth = 8, int depthWidth = 24)
        {
            Book = book ?? throw new ArgumentNullException(nameof(book));
            _spreadWidth = Math.Max(1, spreadWidth);
            _depthWidth = Math.Max(1, depthWidth);
        }

        /// <summary>Performs one action and appends whatever the engine did to <paramref name="events"/>.</summary>
        public void Step(Random random, ICollection<MarketEvent> events)
        {
            var roll = random.NextDouble();

            if (_live.Count > 0 && roll < 0.35)
            {
                Cancel(random, events);
                return;
            }

            if (roll < 0.42)
            {
                Aggress(random, events);
                return;
            }

            Add(random, events);
        }

        private void Add(Random random, ICollection<MarketEvent> events)
        {
            var side = random.Next(2) == 0 ? Side.Bid : Side.Ask;
            var reference = ReferencePrice(side);

            // Passive interest clusters near the touch and thins out behind it, which is roughly
            // the shape a real book has.
            var offset = 1 + (int)(Math.Abs(random.NextDouble() - random.NextDouble()) * _depthWidth);
            var price = side == Side.Bid ? reference - offset : reference + offset;

            if (price < Book.MinPrice || price > Book.MaxPrice)
                return;

            var id = ++_nextId;
            var result = Book.Submit(id, side, OrderType.Limit, TimeInForce.GoodTilCancel,
                price, (uint)random.Next(1, 500), events);

            if (!result.Rejected && result.RestingQuantity > 0)
                _live.Add(id);
        }

        private void Cancel(Random random, ICollection<MarketEvent> events)
        {
            var index = random.Next(_live.Count);
            var id = _live[index];

            // Swap-remove: order within the live list carries no meaning, and this keeps the
            // bookkeeping O(1) so the generator does not dominate what it is meant to drive.
            _live[index] = _live[_live.Count - 1];
            _live.RemoveAt(_live.Count - 1);

            Book.Cancel(id, events);
        }

        private void Aggress(Random random, ICollection<MarketEvent> events)
        {
            var side = random.Next(2) == 0 ? Side.Bid : Side.Ask;
            var opposite = side == Side.Bid ? Side.Ask : Side.Bid;

            if (!Book.TryGetBest(opposite, out var price, out var available) || available == 0)
                return;

            // Usually take part of the touch; occasionally sweep through several levels.
            var quantity = random.NextDouble() < 0.85
                ? (uint)Math.Max(1, random.Next(1, (int)Math.Min(available, 400)))
                : (uint)random.Next(400, 2000);

            Book.Submit(++_nextId, side, OrderType.Limit, TimeInForce.ImmediateOrCancel,
                side == Side.Bid ? price + _spreadWidth : price - _spreadWidth, quantity, events);
        }

        /// <summary>
        /// Where new passive interest is placed relative to: the touch when one exists, otherwise
        /// a synthetic mid so an empty book can seed itself without crossing.
        /// </summary>
        private int ReferencePrice(Side side)
        {
            if (Book.TryGetBest(side, out var own, out _))
                return own;

            if (Book.TryGetBest(side == Side.Bid ? Side.Ask : Side.Bid, out var opposite, out _))
                return side == Side.Bid ? opposite - _spreadWidth : opposite + _spreadWidth;

            return side == Side.Bid ? -_spreadWidth : _spreadWidth;
        }

        /// <summary>Drops references to orders the engine has since removed by trading them out.</summary>
        public void Compact()
        {
            var kept = 0;

            for (var i = 0; i < _live.Count; i++)
            {
                if (Book.Find(_live[i]) is not null)
                    _live[kept++] = _live[i];
            }

            _live.RemoveRange(kept, _live.Count - kept);
        }

        public void Reset()
        {
            Book.Clear();
            _live.Clear();
        }

        private readonly int _spreadWidth;
        private readonly int _depthWidth;
        private readonly List<ulong> _live = new List<ulong>();
        private ulong _nextId;
    }
}
