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
    public interface IOrderbookService : IDisposable
    {
        Task StartAsync();
        Task StopAsync();
        ValueTask OnOrderbookUpdateAsync(MarketData.Common.OrderbookUpdate update);
    }
    public class OrderbookService : Proto.OrderbookService.OrderbookServiceBase, IOrderbookService
    {
        public int Port { get; }
        public OrderbookService(int port, IOrderbookManager orderbookManager)
        {
            Port = port;
            _orderbookManager = orderbookManager;

            _incrementalUpdateTask = ProcessIncrementalUpdatesAsync(new WeakReference<OrderbookService>(this), _orderbookUpdateChannel.Reader, _shutdownSource);
        }

        private static async Task ProcessIncrementalUpdatesAsync(WeakReference<OrderbookService> model, 
            ChannelReader<OrderbookUpdate> reader,
            TaskCompletionSource shutdownSource)
        {
            while (true)
            {
                var pendingClients = new List<ServerClient>();
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

                    try
                    {
                        await service.HandleIncrementalUpdateAsync(update).ConfigureAwait(false);
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

        private async Task HandleIncrementalUpdateAsync(OrderbookUpdate update)
        {
            List<ServerClient> clients = null;

            lock (_clientsLock)
                clients = _clients.Values.Where(i => i.Ids.Contains(update.InstrumentId)).ToList();

            if (!clients.Any())
                return;

            await Task.WhenAll(clients.Select(i => i.SendAsync(update, default))).ConfigureAwait(false);
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
                var client = new ServerClient(context.Peer, responseStream);

                lock (_clientsLock)
                    _clients.Add(client.Host, client);

                Console.WriteLine($"Added client {client.Host}");

                try
                {
                    var streamingCompletionSource = new TaskCompletionSource();
                    using (var streamingCompleteTokenSource = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken))
                    using (var streamingCompleteRegistration = streamingCompleteTokenSource.Token.Register(() => streamingCompletionSource.SetResult()))
                    using (var readClientSubscriptionRequestsTask = ReadClientSubscriptionRequestsAsync(requestStream, client, streamingCompleteTokenSource.Token))
                    {
                        await Task.WhenAny(streamingCompletionSource.Task, readClientSubscriptionRequestsTask).ConfigureAwait(false);

                        if (streamingCompletionSource.Task.IsCompleted)
                            return;

                        await readClientSubscriptionRequestsTask.ConfigureAwait(false);
                    }
                }
                finally
                {
                    lock (_clientsLock)
                        _clients.Remove(client.Host);

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

            if (addedSubscriptions.Any())
                Console.WriteLine($"{client.Host} subscribed to {string.Join(",", addedSubscriptions)}");

            if (removedSubscriptions.Any())
                Console.WriteLine($"{client.Host} unsubscribed from {string.Join(",", removedSubscriptions)}");

            var removedOrderbooks = new List<OrderbookSnapshotUpdate>();
            var addedOrderbooks = new List<OrderbookSnapshotUpdate>();

            foreach (var removedSubscription in removedSubscriptions)
                removedOrderbooks.Add(new OrderbookSnapshotUpdate(removedSubscription));

            foreach (var addedSubscription in addedSubscriptions)
                addedOrderbooks.Add(_orderbookManager.GetSnapshot(addedSubscription));

            var removedOrderbookSendTask = Task.WhenAll(removedOrderbooks.Select(i => client.SendAsync(new OrderbookUpdate(i), default)));
            var addedOrderbookSendTask = Task.WhenAll(addedOrderbooks.Select(i => client.SendAsync(new OrderbookUpdate(i), default)));

            await Task.WhenAll(removedOrderbookSendTask, addedOrderbookSendTask).ConfigureAwait(false);
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

            return _orderbookUpdateChannel.Writer.WriteAsync(update);
        }

        public void Dispose()
        {
            _orderbookUpdateChannel.Writer.Complete();

            _shutdownSource.SetResult();
            _incrementalUpdateTask.GetAwaiter().GetResult();

            StopAsync().GetAwaiter().GetResult();
        }


        private Grpc.Core.Server _server = null;
        private readonly IOrderbookManager _orderbookManager = null;
        private static readonly HashSet<int> _empty = new HashSet<int>();
        private readonly Dictionary<string, ServerClient> _clients = new Dictionary<string, ServerClient>();
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
