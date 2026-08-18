using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MarketData.Common
{
    public record OrderbookSnapshotUpdate(int InstrumentId, IReadOnlyList<OrderbookLevel> Bids, IReadOnlyList<OrderbookLevel> Asks)
    {
        public OrderbookSnapshotUpdate(int InstrumentId) : this(InstrumentId, new List<OrderbookLevel>().AsReadOnly(), new List<OrderbookLevel>().AsReadOnly()) { }
        public static OrderbookSnapshotUpdate Empty { get; } = new OrderbookSnapshotUpdate(0, new List<OrderbookLevel>().AsReadOnly(), new List<OrderbookLevel>().AsReadOnly());
    }
    public record OrderbookIncrementalUpdate(int InstrumentId, OrderbookUpdateType Type, OrderbookLevel Level);
    public record OrderbookUpdate(OrderbookSnapshotUpdate Snapshot, OrderbookIncrementalUpdate Incremental)
    {
        public OrderbookUpdate(OrderbookSnapshotUpdate Snapshot) : this(Snapshot, null) { }
        public OrderbookUpdate(OrderbookIncrementalUpdate Incremental) : this(null, Incremental) { }

        /// <summary>
        /// Monotonic <see cref="System.Diagnostics.Stopwatch"/> timestamp taken when the update was
        /// produced, carried to the subscriber so dissemination latency can be measured end to end.
        /// </summary>
        public long SourceTimestamp { get; init; }
        public bool IsSnapshot => Snapshot is not null;
        public bool IsEmptySnapshot => IsSnapshot && Snapshot.Asks.Count is 0 && Snapshot.Bids.Count is 0;
        public int InstrumentId => Snapshot?.InstrumentId ?? Incremental.InstrumentId;
    }
}
