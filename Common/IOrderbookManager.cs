using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MarketData.Common
{
    public interface IOrderbookManager
    {
        OrderbookSnapshotUpdate GetSnapshot(int instrumentId);

        /// <summary>
        /// Every instrument on the feed. The multicast path needs this to republish full books on
        /// a schedule, since that is the only recovery route available to a gapped subscriber.
        /// </summary>
        IReadOnlyCollection<int> InstrumentIds { get; }
    }
}
