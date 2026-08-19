using Grpc.Core;
using Proto;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using MarketData.Common.Concurrency;
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

        /// <summary>
        /// Registers a producer, returning the handle it must publish through.
        /// </summary>
        /// <remarks>
        /// Called once per matching engine at start-up. With the ring-based queue this hands back a
        /// dedicated single-producer ring, which is what lets the publish path avoid an interlocked
        /// operation entirely; the channel-based queue ignores it and returns a shared handle.
        /// </remarks>
        IUpdateProducer RegisterProducer();
    }

    /// <summary>One matching engine's route into the dissemination path.</summary>
    public interface IUpdateProducer
    {
        /// <summary>Publishes an update. Must be called from one thread only.</summary>
        ValueTask PublishAsync(MarketData.Common.OrderbookUpdate update);
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

        /// <param name="useRingQueue">
        /// Route the engine-to-fan-out hand-off through per-producer lock-free rings instead of a
        /// channel. Both are kept so the two can be measured against each other on the same build;
        /// see BENCHMARKS.md for what the difference turns out to be worth end to end.
        /// </param>
        public OrderbookService(int port, IOrderbookManager orderbookManager, bool verboseLogging = true,
            int queueCapacity = 1024, bool useRingQueue = false)
        {
            Port = port;
            _orderbookManager = orderbookManager;
            VerboseLogging = verboseLogging;
            _queueCapacity = queueCapacity;
            _ringQueue = useRingQueue ? new DisseminationQueue<OrderbookUpdate>() : null;

            _incrementalUpdateTask = _ringQueue is null
                ? ProcessIncrementalUpdatesAsync(new WeakReference<OrderbookService>(this), _orderbookUpdateChannel.Reader, _shutdownSource)
                : ProcessRingUpdatesAsync(new WeakReference<OrderbookService>(this), _ringQueue, _ringShutdown.Token);
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
                catch (ChannelClosedException)
                {
                    // The writer completed: there is nothing left to read and never will be.
                    // Swallowing this and looping is an unbounded spin.
                    return;
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
        /// <summary>
        /// Drains the per-producer rings on a dedicated thread.
        /// </summary>
        /// <remarks>
        /// A long-running thread rather than a pooled task: this loop spins before it sleeps, and
        /// occupying a thread-pool thread that way would starve everything else the pool has to do.
        /// </remarks>
        private static Task ProcessRingUpdatesAsync(WeakReference<OrderbookService> model,
            DisseminationQueue<OrderbookUpdate> queue, CancellationToken token)
        {
            return Task.Factory.StartNew(() =>
            {
                while (!token.IsCancellationRequested)
                {
                    if (!queue.TryTake(out var update, token))
                        return;

                    if (!model.TryGetTarget(out var service))
                        return;

                    try
                    {
                        Interlocked.Decrement(ref service._queuedUpdates);
                        service.Broadcast(update);
                        Interlocked.Increment(ref service._disseminatedUpdates);
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine($"Error in {nameof(ProcessRingUpdatesAsync)}: {e}");
                    }
                    finally
                    {
                        service = null;
                    }
                }
            }, token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        /// <summary>
        /// A producer's private route into the fan-out: its own ring, so publishing needs no
        /// interlocked operation on any shared cursor.
        /// </summary>
        private sealed class RingProducer : IUpdateProducer
        {
            public RingProducer(OrderbookService service, RingBuffer<OrderbookUpdate> ring)
            {
                _service = service;
                _ring = ring;
            }

            public ValueTask PublishAsync(OrderbookUpdate update)
            {
                // Matches the channel transport, which drops until the server is listening. The two
                // paths are compared against each other, so they have to agree on what counts.
                if (_service._server == null)
                    return ValueTask.CompletedTask;

                Interlocked.Increment(ref _service._publishedUpdates);

                _service.RecordQueueDepth(Interlocked.Increment(ref _service._queuedUpdates));

                if (!_ring.TryWrite(update))
                {
                    // Full means the fan-out has fallen an entire ring behind, which is a different
                    // failure from one slow subscriber and is counted separately.
                    Interlocked.Decrement(ref _service._queuedUpdates);
                    Interlocked.Increment(ref _service._droppedUpdates);
                }

                _service._ringQueue.Signal();
                return ValueTask.CompletedTask;
            }

            private readonly OrderbookService _service;
            private readonly RingBuffer<OrderbookUpdate> _ring;
        }

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

                // Only a genuine queue write is a send. Drops are owned by the subscriber's own
                // counter, which GetStatistics folds in - incrementing here as well counted every
                // drop twice.
                if (client.TryEnqueue(message, instrumentId, isSnapshot, isEmptySnapshot) == EnqueueResult.Queued)
                    Interlocked.Increment(ref _sentMessages);
            }
        }

        /// <summary>
        /// A dedicated ring per producer when the ring queue is on; otherwise the shared channel,
        /// which is already safe for many producers.
        /// </summary>
        public IUpdateProducer RegisterProducer()
            => _ringQueue is null ? new ChannelProducer(this) : new RingProducer(this, _ringQueue.AddProducer());

        private sealed class ChannelProducer : IUpdateProducer
        {
            public ChannelProducer(OrderbookService service) => _service = service;

            public ValueTask PublishAsync(OrderbookUpdate update) => _service.OnOrderbookUpdateAsync(update);

            private readonly OrderbookService _service;
        }

        public OrderbookServiceStatistics GetStatistics()
        {
            // Drops live in exactly two places: this service's counter (ring-full drops, plus the
            // final tally of every subscriber that has already disconnected) and the counters of the
            // subscribers still connected. Summing the two is a partition, not a double count.
            var snapshot = _clientSnapshot;
            var clients = snapshot.Length;
            long liveDrops = 0;
            long liveFailures = 0;
            long outboundQueued = 0;
            var maxOutboundQueued = 0;

            for (var i = 0; i < snapshot.Length; i++)
            {
                liveDrops += snapshot[i].DroppedUpdates;
                liveFailures += snapshot[i].FailedSends;

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
                Interlocked.Read(ref _failedSends) + liveFailures,
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

                    // The client is about to leave _clientSnapshot, so GetStatistics will stop
                    // seeing its counter. Fold it in once, here, as it goes.
                    Interlocked.Add(ref _droppedUpdates, client.DroppedUpdates);
                    Interlocked.Add(ref _failedSends, client.FailedSends);

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
            if (client.TryEnqueue(ProtoAdapter.ToProto(update), update.InstrumentId, update.IsSnapshot, update.IsEmptySnapshot) == EnqueueResult.Queued)
                Interlocked.Increment(ref _sentMessages);
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

            RecordQueueDepth(Interlocked.Increment(ref _queuedUpdates));

            return _orderbookUpdateChannel.Writer.WriteAsync(update);
        }

        /// <summary>
        /// Raises the backlog high-water mark to <paramref name="depth"/> if it is a new maximum.
        /// </summary>
        /// <remarks>
        /// Watermark rather than instantaneous depth: a backlog that forms and drains between two
        /// samples is still the fan-out failing to keep up with the matching engine.
        /// <para>
        /// The compare-and-swap is the whole point. Read-then-Exchange looks equivalent but lets two
        /// producers both observe a stale maximum and race to store, so the smaller depth can land
        /// last and erase the larger one - under-reporting the backlog exactly when the fan-out is
        /// falling behind, which is when the number matters. With one ring per producer this path is
        /// genuinely concurrent.
        /// </para>
        /// </remarks>
        private void RecordQueueDepth(long depth)
        {
            long seen;

            while (depth > (seen = Interlocked.Read(ref _peakQueuedUpdates)))
            {
                if (Interlocked.CompareExchange(ref _peakQueuedUpdates, depth, seen) == seen)
                    return;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            // Signal shutdown before completing the writer. The reverse order leaves a window in
            // which the drain loop sees the channel completed but not the shutdown flag, and spins
            // on ChannelClosedException at full CPU.
            _shutdownSource.TrySetResult();
            _ringShutdown.Cancel();
            _ringQueue?.Signal();
            _orderbookUpdateChannel.Writer.TryComplete();
            try
            {
                _incrementalUpdateTask.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                // Expected when the ring loop is cancelled on shutdown.
            }

            _ringQueue?.Dispose();
            _ringShutdown.Dispose();

            StopAsync().GetAwaiter().GetResult();
        }


        private int _disposed;
        private long _queuedUpdates;
        private long _peakQueuedUpdates;
        private long _publishedUpdates;
        private long _disseminatedUpdates;
        private long _sentMessages;
        private long _droppedUpdates;
        private long _failedSends;
        private readonly int _queueCapacity;
        private readonly DisseminationQueue<OrderbookUpdate> _ringQueue;
        private readonly CancellationTokenSource _ringShutdown = new CancellationTokenSource();
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
