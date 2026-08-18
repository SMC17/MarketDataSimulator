using Grpc.Core;
using Proto;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace MarketData.Common.Server
{
    public interface IOrderbookService : IDisposable
    {
        Task StartAsync();
        Task StopAsync();
        ValueTask OnOrderbookUpdateAsync(MarketData.Common.OrderbookUpdate update);
        OrderbookServiceStatistics GetStatistics();
    }

    /// <summary>
    /// Point-in-time view of the dissemination path. <see cref="QueuedUpdates"/> is the important
    /// one under load: a queue that grows without bound means the fan-out is no longer keeping up
    /// with the matching engine, and subscriber latency is about to run away.
    /// </summary>
    public record OrderbookServiceStatistics(
        int ConnectedClients,
        int QueuedUpdates,
        int PeakQueuedUpdates,
        long PublishedUpdates,
        long DisseminatedUpdates,
        long SentMessages,
        long DroppedUpdates,
        long FailedSends,
        long OutboundQueued,
        int MaxOutboundQueued);
    public class OrderbookService : Proto.OrderbookService.OrderbookServiceBase, IOrderbookService
    {
        public int Port { get; }

        /// <summary>
        /// Per-client connect/subscribe logging. Console writes are serialised on a global lock, so
        /// with thousands of subscribers the logging alone dominates the measurement; load runs
        /// turn it off.
        /// </summary>
        public bool VerboseLogging { get; }

        public OrderbookService(int port, IOrderbookManager orderbookManager, bool verboseLogging = true, int queueCapacity = 1024)
        {
            Port = port;
            _orderbookManager = orderbookManager;
            VerboseLogging = verboseLogging;
            _queueCapacity = queueCapacity;

            _incrementalUpdateTask = ProcessIncrementalUpdatesAsync(new WeakReference<OrderbookService>(this), _orderbookUpdateChannel.Reader, _shutdownSource);
        }

        private static async Task ProcessIncrementalUpdatesAsync(WeakReference<OrderbookService> model, 
            ChannelReader<OrderbookUpdate> reader,
            TaskCompletionSource shutdownSource)
        {
            while (true)
            {
                try
                {
                    var readTask = reader.WaitToReadAsync();

                    if (readTask.IsCompleted)
                    {
                        _ = readTask.Result;
                    }
                    else
                    {
                        await Task.WhenAny(shutdownSource.Task, readTask.AsTask()).ConfigureAwait(false);
                    }

                    if (shutdownSource.Task.IsCompleted)
                        return;

                    var update = await reader.ReadAsync().ConfigureAwait(false);

                    if (!model.TryGetTarget(out var service))
                        return;

                    Interlocked.Decrement(ref service._queuedUpdates);

                    try
                    {
                        service.Broadcast(update);
                        Interlocked.Increment(ref service._disseminatedUpdates);
                    }
                    finally
                    {
                        service = null;
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"Error in {nameof(ProcessIncrementalUpdatesAsync)}: {e}");
                }
            }
        }

        /// <summary>
        /// Fans one update out to every subscriber of its instrument. Encoding happens once for the
        /// whole population and each subscriber gets a queue hand-off, so the cost per subscriber is
        /// a set lookup and a bounded-queue write rather than an awaited network round trip.
        /// </summary>
        private void Broadcast(OrderbookUpdate update)
        {
            // Read without a lock: the array is replaced on connect/disconnect, never mutated.
            var clients = _clientSnapshot;

            if (clients.Length == 0)
                return;

            var instrumentId = update.InstrumentId;
            var isSnapshot = update.IsSnapshot;
            var isEmptySnapshot = update.IsEmptySnapshot;
            Proto.OrderbookUpdate message = null;

            for (var i = 0; i < clients.Length; i++)
            {
                var client = clients[i];

                if (!client.IsSubscribedTo(instrumentId) && !isEmptySnapshot)
                    continue;

                message ??= ProtoAdapter.ToProto(update);

                if (client.TryEnqueue(message, instrumentId, isSnapshot, isEmptySnapshot))
                    Interlocked.Increment(ref _sentMessages);
                else
                    Interlocked.Increment(ref _droppedUpdates);
            }
        }

        public OrderbookServiceStatistics GetStatistics()
        {
            var snapshot = _clientSnapshot;
            var clients = snapshot.Length;
            long liveDrops = 0;
            long outboundQueued = 0;
            var maxOutboundQueued = 0;

            for (var i = 0; i < snapshot.Length; i++)
            {
                liveDrops += snapshot[i].DroppedUpdates;

                var queued = snapshot[i].QueuedOutbound;
                outboundQueued += queued;

                if (queued > maxOutboundQueued)
                    maxOutboundQueued = queued;
            }

            return new OrderbookServiceStatistics(
                clients,
                (int)Interlocked.Read(ref _queuedUpdates),
                (int)Interlocked.Exchange(ref _peakQueuedUpdates, 0),
                Interlocked.Read(ref _publishedUpdates),
                Interlocked.Read(ref _disseminatedUpdates),
                Interlocked.Read(ref _sentMessages),
                Interlocked.Read(ref _droppedUpdates) + liveDrops,
                Interlocked.Read(ref _failedSends),
                outboundQueued,
                maxOutboundQueued);
        }

        public Task StartAsync()
        {
            if (_server != null)
                return Task.CompletedTask;

            _server = new Grpc.Core.Server(new ChannelOption[7]
            {
                new ChannelOption("grpc.keepalive_time_ms", 1000),
                new ChannelOption("grpc.keepalive_timeout_ms", 1000),
                new ChannelOption("grpc.keepalive_permit_without_calls", 1),
                new ChannelOption("grpc.http2.max_pings_without_data", 0),
                new ChannelOption("grpc.http2.min_time_between_pings_ms", 1000),
                new ChannelOption("grpc.http2.min_ping_interval_without_data_ms", 1000),
                new ChannelOption("grpc.http2.max_ping_strikes", 0)
            });
            _server.Services.Add(Proto.OrderbookService.BindService(this));
            _server.Ports.Add("0.0.0.0", Port, ServerCredentials.Insecure);
            _server.Start();

            return Task.CompletedTask;
        }

        public override async Task StreamOrderbookUpdates(IAsyncStreamReader<Subscription> requestStream, IServerStreamWriter<Proto.OrderbookUpdate> responseStream, ServerCallContext context)
        {
            try
            {
                var client = new ServerClient(context.Peer, responseStream, _queueCapacity);

                lock (_clientsLock)
                {
                    _clients.Add(client.Id, client);
                    _clientSnapshot = _clients.Values.ToArray();
                }

                if (VerboseLogging)
                    Console.WriteLine($"Added client {client.Host}");

                try
                {
                    using (var streamingCompleteTokenSource = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken))
                    {
                        // Inbound subscription requests and outbound dissemination run concurrently
                        // for the life of the call. Note these are deliberately not disposed:
                        // Task.Dispose throws on a task that has not completed, and WhenAny returns
                        // with one side still running.
                        var readClientSubscriptionRequestsTask = ReadClientSubscriptionRequestsAsync(requestStream, client, streamingCompleteTokenSource.Token);
                        var pumpTask = client.PumpAsync(streamingCompleteTokenSource.Token);

                        await Task.WhenAny(readClientSubscriptionRequestsTask, pumpTask).ConfigureAwait(false);

                        // Whichever side finished, the call is over: release the other one.
                        streamingCompleteTokenSource.Cancel();
                        client.Complete();

                        try
                        {
                            await Task.WhenAll(readClientSubscriptionRequestsTask, pumpTask).ConfigureAwait(false);
                        }
                        catch (Exception)
                        {
                            // The stream is already going away; teardown errors are not interesting.
                        }
                    }
                }
                finally
                {
                    lock (_clientsLock)
                    {
                        _clients.Remove(client.Id);
                        _clientSnapshot = _clients.Values.ToArray();
                    }

                    client.Complete();
                    Interlocked.Add(ref _droppedUpdates, client.DroppedUpdates);

                    if (VerboseLogging)
                        Console.WriteLine($"Removed client {client.Host}");
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"Error in {nameof(StreamOrderbookUpdates)}: {e}");
            }
        }

        private async Task ReadClientSubscriptionRequestsAsync(IAsyncStreamReader<Subscription> requestStream, ServerClient client, CancellationToken token)
        {
            try
            {
                while (await requestStream.MoveNext(token).ConfigureAwait(false))
                {
                    await ProcessSubscribeRequestAsync(requestStream.Current, client, token).ConfigureAwait(false);
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"Error in {nameof(ReadClientSubscriptionRequestsAsync)} for client {client.Host}: {e}");
            }
        }

        private async Task ProcessSubscribeRequestAsync(Subscription current, ServerClient client, CancellationToken token)
        {
            var (addedSubscriptions, removedSubscriptions) = client.Update(
                current.Subscribe?.Ids.ToHashSet(), 
                current.Unsubscribe?.Ids.ToHashSet());

            if (VerboseLogging && addedSubscriptions.Any())
                Console.WriteLine($"{client.Host} subscribed to {string.Join(",", addedSubscriptions)}");

            if (VerboseLogging && removedSubscriptions.Any())
                Console.WriteLine($"{client.Host} unsubscribed from {string.Join(",", removedSubscriptions)}");

            var removedOrderbooks = new List<OrderbookSnapshotUpdate>();
            var addedOrderbooks = new List<OrderbookSnapshotUpdate>();

            foreach (var removedSubscription in removedSubscriptions)
                removedOrderbooks.Add(new OrderbookSnapshotUpdate(removedSubscription));

            foreach (var addedSubscription in addedSubscriptions)
                addedOrderbooks.Add(_orderbookManager.GetSnapshot(addedSubscription));

            var stamp = Stopwatch.GetTimestamp();

            // Routed through the subscriber's own queue like any broadcast update, so a snapshot can
            // never overtake or be overtaken by the incrementals that follow it.
            foreach (var orderbook in removedOrderbooks.Concat(addedOrderbooks))
                EnqueueDirect(client, new OrderbookUpdate(orderbook) { SourceTimestamp = stamp });

            await Task.CompletedTask.ConfigureAwait(false);
        }

        private void EnqueueDirect(ServerClient client, OrderbookUpdate update)
        {
            if (client.TryEnqueue(ProtoAdapter.ToProto(update), update.InstrumentId, update.IsSnapshot, update.IsEmptySnapshot))
                Interlocked.Increment(ref _sentMessages);
            else
                Interlocked.Increment(ref _droppedUpdates);
        }

        public Task StopAsync()
        {
            if (_server == null)
                return Task.CompletedTask;

            return _server.KillAsync();
        }

        public ValueTask OnOrderbookUpdateAsync(OrderbookUpdate update)
        {
            if (_server == null)
                return ValueTask.CompletedTask;

            Interlocked.Increment(ref _publishedUpdates);

            var depth = Interlocked.Increment(ref _queuedUpdates);

            // Watermark rather than instantaneous depth: a backlog that forms and drains between
            // two samples is still the fan-out failing to keep up with the matching engine.
            if (depth > Interlocked.Read(ref _peakQueuedUpdates))
                Interlocked.Exchange(ref _peakQueuedUpdates, depth);

            return _orderbookUpdateChannel.Writer.WriteAsync(update);
        }

        public void Dispose()
        {
            _orderbookUpdateChannel.Writer.Complete();

            _shutdownSource.SetResult();
            _incrementalUpdateTask.GetAwaiter().GetResult();

            StopAsync().GetAwaiter().GetResult();
        }


        private long _queuedUpdates;
        private long _peakQueuedUpdates;
        private long _publishedUpdates;
        private long _disseminatedUpdates;
        private long _sentMessages;
        private long _droppedUpdates;
        private long _failedSends;
        private readonly int _queueCapacity;
        private volatile ServerClient[] _clientSnapshot = Array.Empty<ServerClient>();
        private Grpc.Core.Server _server = null;
        private readonly IOrderbookManager _orderbookManager = null;
        private static readonly HashSet<int> _empty = new HashSet<int>();
        private readonly Dictionary<long, ServerClient> _clients = new Dictionary<long, ServerClient>();
        private readonly object _clientsLock = new object();
        private readonly Task _incrementalUpdateTask = null;
        private readonly TaskCompletionSource _shutdownSource = new TaskCompletionSource();
        private readonly Channel<OrderbookUpdate> _orderbookUpdateChannel = System.Threading.Channels.Channel.CreateUnbounded<OrderbookUpdate>(new UnboundedChannelOptions()
        {
            SingleReader = true,
            SingleWriter = false,
        });
    }
}
