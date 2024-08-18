using MarketData.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MarketData.Server
{
    internal class OrderbookLevelComparer : IComparer<OrderbookLevel>
    {
        public static OrderbookLevelComparer BidComparer { get; } = new OrderbookLevelComparer(true);
        public static OrderbookLevelComparer AskComparer { get; } = new OrderbookLevelComparer(false);

        private OrderbookLevelComparer(bool isBuy) => _isBuy = isBuy;            
        public int Compare(OrderbookLevel x, OrderbookLevel y)
        {
            if (x.Price == y.Price)
                return 0;
            else if (x.Price < y.Price)
                return _isBuy ? -1 : 1;
            else
                return _isBuy ? 1 : -1;
        }

        private readonly bool _isBuy;
    }
}
