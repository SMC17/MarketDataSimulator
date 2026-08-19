using MarketData.Common.Books;
using System;
using System.Collections.Generic;

namespace MarketData.Common.Matching
{
    /// <summary>
    /// An order-by-order limit order book with price-time priority matching.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three structures, each covering an operation the others are bad at:
    /// </para>
    /// <list type="bullet">
    /// <item><b>An id map</b> from order id to the order object, so a cancel goes straight to its
    /// target. This is the operation that dominates real order flow - the great majority of
    /// messages an exchange receives are cancels - so it is the one that must not scan.</item>
    /// <item><b>An intrusive FIFO per price level.</b> Time priority is arrival order, so a queue
    /// is the natural shape; keeping the links on the order itself makes unlinking O(1) once the
    /// id map has found it.</item>
    /// <item><b>A price-indexed ladder with a bitset</b> for locating the touch. Matching always
    /// starts at the best price and walks outwards, and a hardware bit-scan finds the next
    /// occupied price over 64 slots per instruction.</item>
    /// </list>
    /// <para>
    /// The result is O(1) add, O(1) cancel, O(1) reduce, and matching linear only in the number of
    /// orders actually filled - never in the size of the book. The measured cost of each is in
    /// BENCHMARKS.md.
    /// </para>
    /// <para>
    /// Not thread-safe, and deliberately so. An exchange's matching core is single-threaded
    /// because sequence is the product: every participant must agree on what happened in what
    /// order, and that is far easier to guarantee - and faster - than coordinating locks around a
    /// structure this hot. Concurrency belongs on either side of the core, not inside it.
    /// </para>
    /// </remarks>
    public sealed class LimitOrderBook
    {
        public int MinPrice { get; }
        public int MaxPrice { get; }

        /// <summary>Resting orders across both sides.</summary>
        public int OrderCount => _orders.Count;

        public LimitOrderBook(int minPrice, int maxPrice)
        {
            if (maxPrice < minPrice)
                throw new ArgumentException("maxPrice must not be below minPrice.", nameof(maxPrice));

            MinPrice = minPrice;
            MaxPrice = maxPrice;
            _band = maxPrice - minPrice + 1;
            _index = new PriceIndex(_band);
            _levels = new OrderLevel[2][];

            for (var side = 0; side < 2; side++)
                _levels[side] = new OrderLevel[_band];
        }

        // ------------------------------------------------------------------ queries

        public bool TryGetBest(Side side, out int price, out ulong quantity)
        {
            var slot = _index.Touch(side);

            if (slot == PriceIndex.None)
            {
                price = 0;
                quantity = 0;
                return false;
            }

            var level = _levels[(int)side][slot];
            price = level.Price;
            quantity = level.TotalQuantity;
            return true;
        }

        public Order Find(ulong orderId) => _orders.TryGetValue(orderId, out var order) ? order : null;

        /// <summary>
        /// Every resting order on a side, in the exact order it would be filled: price first, then
        /// arrival. Walking this is how the book's priority can be inspected and compared against
        /// a reference implementation.
        /// </summary>
        public IEnumerable<Order> OrdersInPriority(Side side)
        {
            var levels = _levels[(int)side];
            var slot = _index.Touch(side);

            while (slot != PriceIndex.None)
            {
                for (var order = levels[slot].Head; order is not null; order = order.Next)
                    yield return order;

                slot = _index.Outward(side, slot);
            }
        }

        /// <summary>
        /// Aggregate resting size at one price. This is how a depth feed is derived from an
        /// order-by-order book: after each event, the level it touched is re-read and republished.
        /// </summary>
        public ulong QuantityAt(Side side, int price)
        {
            if (!InBand(price))
                return 0;

            var level = _levels[(int)side][price - MinPrice];
            return level?.TotalQuantity ?? 0;
        }

        /// <summary>The order at the front of the queue at <paramref name="price"/>, if any.</summary>
        public Order FirstOrderAt(Side side, int price)
            => InBand(price) ? _levels[(int)side][price - MinPrice]?.Head : null;

        /// <summary>Aggregated depth, touch first - the input to the depth-limited public feed.</summary>
        public int CopyDepth(Side side, Span<PriceLevel> destination)
        {
            var written = 0;
            var slot = _index.Touch(side);
            var levels = _levels[(int)side];

            while (written < destination.Length && slot != PriceIndex.None)
            {
                var level = levels[slot];
                destination[written++] = new PriceLevel(level.Price, (uint)Math.Min(level.TotalQuantity, uint.MaxValue));

                slot = _index.Outward(side, slot);
            }

            return written;
        }

        // ------------------------------------------------------------------ commands

        /// <summary>
        /// Matches an incoming order against the book and rests any remainder.
        /// </summary>
        /// <param name="events">Appended to, never read. Caller-supplied so the path allocates nothing.</param>
        public SubmitResult Submit(ulong orderId, Side side, OrderType type, TimeInForce timeInForce,
            int price, uint quantity, ICollection<MarketEvent> events)
        {
            if (quantity == 0 || _orders.ContainsKey(orderId) || (type == OrderType.Limit && !InBand(price)))
            {
                events?.Add(MarketEvent.Rejected(orderId, side, price, quantity));
                return new SubmitResult(orderId, 0, 0, Rejected: true);
            }

            // A market order is a limit order priced through the whole book.
            var limit = type == OrderType.Market
                ? (side == Side.Bid ? MaxPrice : MinPrice)
                : price;

            // Fill-or-kill must be decided before anything is mutated, so ask the book what is
            // available first rather than matching and unwinding.
            if (timeInForce == TimeInForce.FillOrKill && AvailableAgainst(side, limit) < quantity)
            {
                events?.Add(MarketEvent.Rejected(orderId, side, price, quantity));
                return new SubmitResult(orderId, 0, 0, Rejected: true);
            }

            var remaining = Match(orderId, side, limit, quantity, events);
            var filled = quantity - remaining;

            if (remaining == 0)
                return new SubmitResult(orderId, filled, 0, Rejected: false);

            // Anything that cannot rest ends here: market orders never rest, and IOC cancels.
            if (type == OrderType.Market || timeInForce != TimeInForce.GoodTilCancel)
                return new SubmitResult(orderId, filled, 0, Rejected: false);

            Rest(orderId, side, price, remaining, events);
            return new SubmitResult(orderId, filled, remaining, Rejected: false);
        }

        /// <summary>Removes a resting order. O(1).</summary>
        public bool Cancel(ulong orderId, ICollection<MarketEvent> events)
        {
            if (!_orders.TryGetValue(orderId, out var order))
                return false;

            events?.Add(MarketEvent.Cancelled(order));
            Unlink(order);
            _orders.Remove(orderId);
            ReleaseOrder(order);
            return true;
        }

        /// <summary>
        /// Changes a resting order's size in place.
        /// </summary>
        /// <remarks>
        /// A reduction keeps queue position; an increase does not. That is the rule real venues
        /// use, and it is not arbitrary - letting an order grow while holding its place would let
        /// a participant reserve priority with a small order and claim it later with a large one.
        /// An increase is therefore a cancel and a fresh arrival at the back of the queue.
        /// </remarks>
        public bool Reduce(ulong orderId, uint newQuantity, ICollection<MarketEvent> events)
        {
            if (!_orders.TryGetValue(orderId, out var order))
                return false;

            if (newQuantity == 0)
                return Cancel(orderId, events);

            if (newQuantity > order.Remaining)
                return false;

            order.Level.TotalQuantity -= order.Remaining - newQuantity;
            order.Remaining = newQuantity;
            events?.Add(MarketEvent.Reduced(order));
            return true;
        }

        public void Clear()
        {
            _orders.Clear();

            _index.Clear();

            for (var side = 0; side < 2; side++)
                Array.Clear(_levels[side], 0, _levels[side].Length);

            _pool.Clear();
            _orderPool.Clear();
        }

        // ------------------------------------------------------------------ matching

        /// <summary>
        /// Walks the opposite side from its touch, filling in price then time order.
        /// </summary>
        /// <remarks>
        /// The cost is proportional to the number of orders actually filled, not to the size of
        /// the book: each iteration either exhausts a resting order and unlinks it, or exhausts
        /// the incoming order and stops.
        /// </remarks>
        private uint Match(ulong aggressorId, Side side, int limit, uint quantity, ICollection<MarketEvent> events)
        {
            var opposite = side == Side.Bid ? Side.Ask : Side.Bid;
            var levels = _levels[(int)opposite];
            var remaining = quantity;

            while (remaining > 0)
            {
                var slot = _index.Touch(opposite);

                if (slot == PriceIndex.None)
                    break;

                var level = levels[slot];

                // Price priority: stop as soon as the touch is no longer acceptable.
                if (!Crosses(side, level.Price, limit))
                    break;

                while (remaining > 0 && level.Head is not null)
                {
                    // Time priority: the head of the queue is the oldest order at this price.
                    var resting = level.Head;
                    var fill = Math.Min(remaining, resting.Remaining);

                    resting.Remaining -= fill;
                    level.TotalQuantity -= fill;
                    remaining -= fill;

                    events?.Add(MarketEvent.Traded(resting, aggressorId, fill));

                    if (resting.Remaining == 0)
                    {
                        Unlink(resting);
                        _orders.Remove(resting.Id);
                        ReleaseOrder(resting);
                    }
                }
            }

            return remaining;
        }

        /// <summary>Total size available to an aggressor at or better than <paramref name="limit"/>.</summary>
        private ulong AvailableAgainst(Side side, int limit)
        {
            var opposite = side == Side.Bid ? Side.Ask : Side.Bid;
            var levels = _levels[(int)opposite];
            var slot = _index.Touch(opposite);
            ulong available = 0;

            while (slot != PriceIndex.None)
            {
                var level = levels[slot];

                if (!Crosses(side, level.Price, limit))
                    break;

                available += level.TotalQuantity;
                slot = _index.Outward(opposite, slot);
            }

            return available;
        }

        /// <summary>True when an aggressor on <paramref name="side"/> would accept <paramref name="restingPrice"/>.</summary>
        private static bool Crosses(Side side, int restingPrice, int limit)
            => side == Side.Bid ? restingPrice <= limit : restingPrice >= limit;

        private void Rest(ulong orderId, Side side, int price, uint quantity, ICollection<MarketEvent> events)
        {
            var slot = price - MinPrice;
            var index = (int)side;
            var level = _levels[index][slot];

            if (level is null)
            {
                level = Allocate();
                level.Price = price;
                _levels[index][slot] = level;
            }

            var order = AllocateOrder();
            order.Id = orderId;
            order.Side = side;
            order.Price = price;
            order.Quantity = quantity;
            order.Remaining = quantity;
            order.Level = level;

            // Joins at the tail: newest order, worst time priority.
            if (level.Tail is null)
            {
                level.Head = order;
                level.Tail = order;
                _index.Occupy(side, slot);
            }
            else
            {
                order.Previous = level.Tail;
                level.Tail.Next = order;
                level.Tail = order;
            }

            level.TotalQuantity += quantity;
            level.OrderCount++;
            _orders[orderId] = order;

            events?.Add(MarketEvent.Added(order));
        }

        /// <summary>Removes an order from its level's queue. O(1) - the whole point of the intrusive list.</summary>
        private void Unlink(Order order)
        {
            var level = order.Level;

            if (order.Previous is null)
                level.Head = order.Next;
            else
                order.Previous.Next = order.Next;

            if (order.Next is null)
                level.Tail = order.Previous;
            else
                order.Next.Previous = order.Previous;

            level.TotalQuantity -= order.Remaining;
            level.OrderCount--;

            order.Previous = null;
            order.Next = null;
            order.Level = null;

            if (level.OrderCount != 0)
                return;

            // Last order at this price: the level leaves the book.
            var index = (int)order.Side;
            var slot = level.Price - MinPrice;

            _index.Vacate(order.Side, slot);
            _levels[index][slot] = null;
            Release(level);
        }

        private bool InBand(int price) => price >= MinPrice && price <= MaxPrice;

        /// <summary>
        /// Orders and levels are recycled rather than re-allocated.
        /// </summary>
        /// <remarks>
        /// A venue's steady state is a torrent of arrivals and cancellations at roughly constant
        /// book size, so a fresh object per order would hand the collector a few hundred megabytes
        /// an hour of pure churn. The pauses that causes land at arbitrary moments in the matching
        /// path, which is the one place that cannot afford them. Recycling makes the steady state
        /// allocate nothing.
        /// <para>
        /// Safe because the book owns an order's whole lifetime: it is released only after being
        /// unlinked from its level and removed from the id map, so nothing can still reach it.
        /// </para>
        /// </remarks>
        private Order AllocateOrder()
        {
            if (_orderPool.Count == 0)
                return new Order();

            var order = _orderPool[_orderPool.Count - 1];
            _orderPool.RemoveAt(_orderPool.Count - 1);
            return order;
        }

        private void ReleaseOrder(Order order)
        {
            if (_orderPool.Count >= PoolLimit)
                return;

            order.Previous = null;
            order.Next = null;
            order.Level = null;
            _orderPool.Add(order);
        }

        private OrderLevel Allocate()
        {
            if (_pool.Count == 0)
                return new OrderLevel();

            var level = _pool[_pool.Count - 1];
            _pool.RemoveAt(_pool.Count - 1);
            level.Reset();
            return level;
        }

        private void Release(OrderLevel level)
        {
            level.Reset();

            if (_pool.Count < PoolLimit)
                _pool.Add(level);
        }

        private const int PoolLimit = 4096;

        private readonly int _band;
        private readonly PriceIndex _index;
        private readonly OrderLevel[][] _levels;
        private readonly Dictionary<ulong, Order> _orders = new Dictionary<ulong, Order>();
        private readonly List<OrderLevel> _pool = new List<OrderLevel>();
        private readonly List<Order> _orderPool = new List<Order>();
    }
}
