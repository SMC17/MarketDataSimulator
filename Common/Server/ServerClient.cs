using Grpc.Core;
using Nito.AsyncEx;
using Proto;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MarketData.Common.Server
{
    internal class ServerClient
    {
        public string Host { get; }
        public IReadOnlySet<int> Ids
        {
            get
            {
                lock (_subscriptionsLock)
                    return _subscriptions.Keys.ToHashSet();
            }
        }

        public ServerClient(string host, IServerStreamWriter<Proto.OrderbookUpdate> stream)
        {
            Host = host;

            _stream = stream;
        }

        public (IReadOnlySet<int> Added, IReadOnlySet<int> Removed) Update(HashSet<int> addedSubscriptions, HashSet<int> removedSubscriptions)
        {
            addedSubscriptions ??= _empty;
            removedSubscriptions ??= _empty;

            using (_subscriptionsLock.Lock())
            {
                var subscriptions = _subscriptions.Keys.ToHashSet();

                addedSubscriptions.ExceptWith(subscriptions);
                removedSubscriptions.IntersectWith(subscriptions);

                foreach (var addedSubscription in addedSubscriptions)
                    _subscriptions.Add(addedSubscription, true);

                foreach (var removedSubscription in removedSubscriptions)
                    _subscriptions.Remove(removedSubscription);

                return (addedSubscriptions, removedSubscriptions);
            }
        }

        public async Task SendAsync(OrderbookUpdate update, CancellationToken token)
        {
            using (await _subscriptionsLock.LockAsync().ConfigureAwait(false))
            {
                if (!_subscriptions.TryGetValue(update.InstrumentId, out var snapshot) && !update.IsEmptySnapshot)
                    return;

                if (snapshot && !update.IsSnapshot)
                    return;

                if (snapshot && update.IsSnapshot)
                    _subscriptions[update.InstrumentId] = false;

                Proto.OrderbookUpdate response = new Proto.OrderbookUpdate()
                {
                    InstrumentId = update.InstrumentId,
                };

                if (update.IsSnapshot)
                {
                    response.Snapshot = new Proto.OrderbookSnapshotUpdate();
                    response.Snapshot.Asks.AddRange(update.Snapshot.Asks.Select(ProtoAdapter.ToSnapshotLevel));
                    response.Snapshot.Bids.AddRange(update.Snapshot.Bids.Select(ProtoAdapter.ToSnapshotLevel));
                }
                else
                {
                    response.Incremental = new Proto.OrderbookIncrementalUpdate();
                    response.Incremental.Update = ProtoAdapter.ToIncrementalLevel(update.Incremental);
                }

                await _stream.WriteAsync(response, token).ConfigureAwait(false);
            }
        }

        private static readonly HashSet<int> _empty = new HashSet<int>();
        private readonly IServerStreamWriter<Proto.OrderbookUpdate> _stream = null;
        private readonly Dictionary<int, bool> _subscriptions = new Dictionary<int, bool>();
        private readonly AsyncLock _subscriptionsLock = new AsyncLock();
    }
}
