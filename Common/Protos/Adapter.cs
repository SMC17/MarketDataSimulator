using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MarketData.Common
{
    public class ProtoAdapter
    {
        public static OrderbookUpdate FromProto(Proto.OrderbookUpdate update)
        {
            static OrderbookLevel ToLevel(Proto.OrderbookLevelUpdate level)
                => new OrderbookLevel(level.Level.Price, level.Level.IsBuy, level.Level.Quantity);
            static OrderbookUpdateType ToType(Proto.OrderbookLevelUpdateType type)
            {
                return type switch
                {
                    Proto.OrderbookLevelUpdateType.Add => OrderbookUpdateType.Add,
                    Proto.OrderbookLevelUpdateType.Replace => OrderbookUpdateType.Replace,
                    Proto.OrderbookLevelUpdateType.Remove => OrderbookUpdateType.Remove,
                    _ => throw new InvalidOperationException($"Unknown Type ({type})"),
                };
            }

            if (update.UpdateCase is Proto.OrderbookUpdate.UpdateOneofCase.Snapshot)
            {
                var bids = update.Snapshot.Bids.Select(ToLevel);
                var asks = update.Snapshot.Asks.Select(ToLevel);
                return new OrderbookUpdate(new OrderbookSnapshotUpdate(update.InstrumentId,
                    bids.ToList().AsReadOnly(),
                    asks.ToList().AsReadOnly()));
            }
            else if (update.UpdateCase is Proto.OrderbookUpdate.UpdateOneofCase.Incremental)
            {
                return new OrderbookUpdate(new OrderbookIncrementalUpdate(update.InstrumentId,
                    ToType(update.Incremental.Update.UpdateType), ToLevel(update.Incremental.Update)));
            }
            else throw new InvalidOperationException($"Unknown Update Type ({update.UpdateCase})");
        }
        public static Proto.OrderbookLevelUpdate ToSnapshotLevel(OrderbookLevel i) =>
                new Proto.OrderbookLevelUpdate() { UpdateType = Proto.OrderbookLevelUpdateType.Add, Level = new Proto.OrderbookLevel() { IsBuy = i.IsBuy, Price = i.Price, Quantity = i.Quantity } };
        public static Proto.OrderbookLevelUpdate ToIncrementalLevel(OrderbookIncrementalUpdate i)
                => new Proto.OrderbookLevelUpdate() { UpdateType = ToType(i.Type), Level = new Proto.OrderbookLevel() { IsBuy = i.Level.IsBuy, Price = i.Level.Price, Quantity = i.Level.Quantity } };
        public static Proto.OrderbookLevelUpdateType ToType(MarketData.Common.OrderbookUpdateType type)
        {
            return type switch
            {
                OrderbookUpdateType.Replace => Proto.OrderbookLevelUpdateType.Replace,
                OrderbookUpdateType.Add => Proto.OrderbookLevelUpdateType.Add,
                OrderbookUpdateType.Remove => Proto.OrderbookLevelUpdateType.Remove,
                _ => throw new InvalidOperationException($"Unknown type ({type}"),
            };
        }

    }
}
