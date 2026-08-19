using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MarketData.Common
{
    public enum OrderbookUpdateType
    {
        Invalid,
        Add,
        Replace,
        Remove,
    }
    public record OrderbookLevel(int Price, bool IsBuy, uint Quantity);
}
