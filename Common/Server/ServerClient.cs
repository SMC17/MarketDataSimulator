using Grpc.Core;
using Proto;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace MarketData.Common.Server
{
    /// <summary>
    /// One subscriber's view of the feed: its subscription set, its outbound queue, and the pump
    /// that drains that queue onto its gRPC stream.
    /// <para>
    /// The broadcast thread never touches the network here. It enqueues and moves on, so a single
    /// slow or stalled subscriber cannot hold up dissemination to everybody else - which is the
    /// behaviour an exchange feed needs and the reason the queue is bounded rather than unbounded.
    /// </para>
    /// </summary>
    internal sealed class ServerClient
    {
        public string Host { get; }

        /// <summary>
        /// Identifies the subscription stream, not the remote address. A single host may hold many
        /// concurrent streams (and HTTP/2 multiplexes them onto one connection, giving them all the
        /// same peer), so keying the client table by address dropped every stream after the first.
        /// </summary>
        public long Id { get; } = Interlocked.Increment(ref _nextId);

        /// <summary>
        /// Updates queued for this subscriber but not yet written to its stream. Depth here - as
        /// opposed to depth on the shared update queue - localises a backlog to this one subscriber
        /// rather than to the dissemination path.
        /// </summary>
        public int QueuedOutbound => _outbound.Reader.Count;

        /// <summary>Updates discarded because this subscriber could not keep up.</summary>
        public long DroppedUpdates => Interlocked.Read(ref _droppedUpdates);

        public IReadOnlySet<int> Ids => _subscribedIds;

        public ServerClient(string host, IServerStreamWriter<Proto.OrderbookUpdate> stream, int queueCapacity)
        {
            Host = host;

            _stream = stream;
            _outbound = System.Threading.Channels.Channel.CreateBounded<Proto.OrderbookUpdate>(new BoundedChannelOptions(queueCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait, // never actually waits; we only TryWrite
            });
        }

        /// <summary>
        /// Lock-free subscription test for the broadcast filter. The set is replaced wholesale on
        /// subscription changes rather than mutated, so readers on the hot path need no lock and
        /// allocate nothing.
        /// </summary>
        public bool IsSubscribedTo(int instrumentId) => _subscribedIds.Contains(instrumentId);

        public (IReadOnlySet<int> Added, IReadOnlySet<int> Removed) Update(HashSet<int> addedSubscriptions, HashSet<int> removedSubscriptions)
        {
            addedSubscriptions ??= _empty;
            removedSubscriptions ??= _empty;

            lock (_lock)
            {
                var subscriptions = _subscriptions.Keys.ToHashSet();

                addedSubscriptions.ExceptWith(subscriptions);
                removedSubscriptions.IntersectWith(subscriptions);

                foreach (var addedSubscription in addedSubscriptions)
                    _subscriptions.Add(addedSubscription, true);

                foreach (var removedSubscription in removedSubscriptions)
                    _subscriptions.Remove(removedSubscription);

                if (addedSubscriptions.Any() || removedSubscriptions.Any())
                    _subscribedIds = _subscriptions.Keys.ToHashSet();

                return (addedSubscriptions, removedSubscriptions);
            }
        }

        /// <summary>
        /// Queues an already-encoded update for this subscriber, applying the snapshot gate: a newly
        /// subscribed instrument stays suppressed until its first snapshot arrives, so a client never
        /// sees incrementals against a book it has not been given.
        /// </summary>
        /// <returns><c>true</c> if the update was queued; <c>false</c> if it was dropped.</returns>
        public bool TryEnqueue(Proto.OrderbookUpdate message, int instrumentId, bool isSnapshot, bool isEmptySnapshot)
        {
            lock (_lock)
            {
                if (!_subscriptions.TryGetValue(instrumentId, out var awaitingSnapshot) && !isEmptySnapshot)
                    return true;

                if (awaitingSnapshot && !isSnapshot)
                    return true;

                if (_outbound.Writer.TryWrite(message))
                {
                    if (awaitingSnapshot && isSnapshot)
                        _subscriptions[instrumentId] = false;

                    return true;
                }

                // TryWrite also fails on a completed queue, which means the subscriber is on its way
                // out rather than falling behind. Only the latter is a dropped update.
                if (_completed)
                    return true;

                // Queue full: this subscriber is not draining fast enough. Drop the update and put
                // the instrument back into snapshot-recovery so the client resynchronises from the
                // next full book rather than applying incrementals across a gap.
                if (_subscriptions.ContainsKey(instrumentId))
                    _subscriptions[instrumentId] = true;

                Interlocked.Increment(ref _droppedUpdates);

                return false;
            }
        }

        /// <summary>Drains the outbound queue onto the stream until the call ends.</summary>
        public async Task PumpAsync(CancellationToken token)
        {
            try
            {
                await foreach (var message in _outbound.Reader.ReadAllAsync(token).ConfigureAwait(false))
                {
                    // No token here: the C-core implementation rejects cancellation of stream
                    // writes outright. Cancellation is applied to the queue read instead.
                    await _stream.WriteAsync(message).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // The call ended; nothing further to write.
            }
            catch (Exception)
            {
                // The subscriber's stream is gone; the call teardown path removes it.
            }
        }

        public void Complete()
        {
            _completed = true;
            _outbound.Writer.TryComplete();
        }

        private static long _nextId;
        private static readonly HashSet<int> _empty = new HashSet<int>();
        private long _droppedUpdates;
        private volatile bool _completed;
        private readonly IServerStreamWriter<Proto.OrderbookUpdate> _stream = null;
        private readonly Channel<Proto.OrderbookUpdate> _outbound = null;
        private readonly Dictionary<int, bool> _subscriptions = new Dictionary<int, bool>();
        private volatile HashSet<int> _subscribedIds = new HashSet<int>();
        private readonly object _lock = new object();
    }
}
