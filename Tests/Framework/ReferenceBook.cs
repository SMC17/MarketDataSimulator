using MarketData.Common.Books;
using MarketData.Common.Matching;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MarketData.Tests.Framework
{
    /// <summary>
    /// A deliberately naive order book: a flat list of orders, scanned linearly for everything.
    /// </summary>
    /// <remarks>
    /// Written to be obviously correct rather than fast. Price-time priority is expressed
    /// literally - sort by price, then by arrival - so the rule can be read straight off the code
    /// and checked by eye. The optimised book is then held against it by differential testing,
    /// which is the only way to be confident that an intrusive list, a bitset price index and an
    /// id map really do add up to the same semantics.
    /// </remarks>
    public sealed class ReferenceBook
    {
        private sealed record Resting(ulong Id, Side Side, int Price, ulong Sequence)
        {
            public uint Remaining { get; set; }
        }

        private readonly List<Resting> _orders = new List<Resting>();
        private ulong _sequence;

        public int OrderCount => _orders.Count;

        public SubmitResult Submit(ulong orderId, Side side, OrderType type, TimeInForce timeInForce,
            int price, uint quantity, int minPrice, int maxPrice, ICollection<MarketEvent> events)
        {
            if (orderId == 0 || quantity == 0 || (byte)side > (byte)Side.Ask ||
                (byte)type > (byte)OrderType.MarketToLimit ||
                (byte)timeInForce > (byte)TimeInForce.GoodTilCrossing ||
                _orders.Any(o => o.Id == orderId) ||
                (type == OrderType.Limit && (price < minPrice || price > maxPrice)) ||
                (type == OrderType.MarketToLimit && timeInForce != TimeInForce.GoodTilCancel) ||
                (timeInForce == TimeInForce.GoodTilCrossing && type != OrderType.Limit))
            {
                events?.Add(MarketEvent.Rejected(orderId, side, price, quantity));
                return new SubmitResult(orderId, 0, 0, true);
            }

            var limit = price;
            var restingPrice = price;

            if (type == OrderType.Market)
            {
                limit = side == Side.Bid ? maxPrice : minPrice;
            }
            else if (type == OrderType.MarketToLimit)
            {
                var touch = _orders
                    .Where(o => o.Side != side)
                    .OrderBy(o => side == Side.Bid ? o.Price : -o.Price)
                    .ThenBy(o => o.Sequence)
                    .FirstOrDefault();

                if (touch is null)
                {
                    events?.Add(MarketEvent.Rejected(orderId, side, price, quantity));
                    return new SubmitResult(orderId, 0, 0, true);
                }

                limit = touch.Price;
                restingPrice = limit;
            }

            bool Crosses(Resting o) => o.Side != side &&
                (side == Side.Bid ? o.Price <= limit : o.Price >= limit);

            if (timeInForce == TimeInForce.GoodTilCrossing && _orders.Any(Crosses))
            {
                events?.Add(MarketEvent.Rejected(orderId, side, price, quantity));
                return new SubmitResult(orderId, 0, 0, true);
            }

            if (timeInForce == TimeInForce.FillOrKill)
            {
                ulong available = 0;

                foreach (var o in _orders.Where(Crosses))
                    available += o.Remaining;

                if (available < quantity)
                {
                    events?.Add(MarketEvent.Rejected(orderId, side, price, quantity));
                    return new SubmitResult(orderId, 0, 0, true);
                }
            }

            var remaining = quantity;

            while (remaining > 0)
            {
                // Price first, then time: exactly the rule, spelled out.
                var candidates = _orders.Where(Crosses).ToList();

                if (candidates.Count == 0)
                    break;

                var best = candidates
                    .OrderBy(o => side == Side.Bid ? o.Price : -o.Price)
                    .ThenBy(o => o.Sequence)
                    .First();

                var fill = Math.Min(remaining, best.Remaining);
                best.Remaining -= fill;
                remaining -= fill;

                events?.Add(new MarketEvent(MarketEventType.Traded, best.Id, best.Side, best.Price, fill, orderId));

                if (best.Remaining == 0)
                    _orders.Remove(best);
            }

            var filled = quantity - remaining;

            if (remaining == 0 || type == OrderType.Market ||
                timeInForce is TimeInForce.ImmediateOrCancel or TimeInForce.FillOrKill)
                return new SubmitResult(orderId, filled, 0, false);

            var rested = new Resting(orderId, side, restingPrice, ++_sequence) { Remaining = remaining };
            _orders.Add(rested);
            events?.Add(new MarketEvent(MarketEventType.Added, orderId, side, restingPrice, remaining, 0));

            return new SubmitResult(orderId, filled, remaining, false);
        }

        public bool Cancel(ulong orderId, ICollection<MarketEvent> events)
        {
            var order = _orders.FirstOrDefault(o => o.Id == orderId);

            if (order is null)
                return false;

            events?.Add(new MarketEvent(MarketEventType.Cancelled, order.Id, order.Side, order.Price, order.Remaining, 0));
            _orders.Remove(order);
            return true;
        }

        public bool Reduce(ulong orderId, uint newQuantity, ICollection<MarketEvent> events)
        {
            var order = _orders.FirstOrDefault(o => o.Id == orderId);

            if (order is null)
                return false;

            if (newQuantity == 0)
                return Cancel(orderId, events);

            if (newQuantity > order.Remaining)
                return false;

            order.Remaining = newQuantity;
            events?.Add(new MarketEvent(MarketEventType.Reduced, order.Id, order.Side, order.Price, order.Remaining, 0));
            return true;
        }

        /// <summary>Aggregated depth, touch first.</summary>
        public List<PriceLevel> Depth(Side side, int maxLevels)
            => _orders
                .Where(o => o.Side == side)
                .GroupBy(o => o.Price)
                .OrderBy(g => side == Side.Bid ? -g.Key : g.Key)
                .Take(maxLevels)
                .Select(g => new PriceLevel(g.Key, (uint)g.Sum(o => (long)o.Remaining)))
                .ToList();

        /// <summary>Every resting order in strict price-time order, for comparing queue position.</summary>
        public List<(ulong Id, Side Side, int Price, uint Remaining)> Queue(Side side)
            => _orders
                .Where(o => o.Side == side)
                .OrderBy(o => side == Side.Bid ? -o.Price : o.Price)
                .ThenBy(o => o.Sequence)
                .Select(o => (o.Id, o.Side, o.Price, o.Remaining))
                .ToList();
    }
}
