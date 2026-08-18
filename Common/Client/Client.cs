using Grpc.Core;
using Grpc.Net.Client;
using Proto;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace MarketData.Common.Client
{
    public class Client : Proto.OrderbookService.OrderbookServiceClient, IDisposable
    {
        public Client(GrpcChannel channel) : base(channel)
        {
            _streamingTask = StreamAsync(this, _subscriptionChannel.Reader, _shutdownSource);
        }

        private static async Task StreamAsync(Client client, 
            ChannelReader<(bool Subscribe, int InstrumentId)> reader, 
            TaskCompletionSource shutdownSource)
        {
            using (var streaming = client.StreamOrderbookUpdates())
            {
                _ = HandleResponsesAsync(streaming.ResponseStream, shutdownSource);
                
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

                        var subscription = await reader.ReadAsync().ConfigureAwait(false);

                        var request = new Subscription();

                        if (subscription.Subscribe)
                        {
                            request.Subscribe = new SubscribeRequest();
                            request.Subscribe.Ids.Add(subscription.InstrumentId);
                        }
                        else
                        {
                            request.Unsubscribe = new UnsubscribeRequest();
                            request.Unsubscribe.Ids.Add(subscription.InstrumentId);
                        }

                        await streaming.RequestStream.WriteAsync(request).ConfigureAwait(false);
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine($"Error in {nameof(StreamAsync)}: {e}");
                    }
                }
            }
        }

        private static async Task HandleResponsesAsync(IAsyncStreamReader<Proto.OrderbookUpdate> responseStream, TaskCompletionSource shutdownSource)
        {
            // A MoveNext() around ReadAllAsync() consumed and discarded one message per outer
            // iteration - in practice the initial snapshot - so enumerate the stream just once.
            {
                await foreach (var response in responseStream.ReadAllAsync().ConfigureAwait(false))
                {
                    var update = ProtoAdapter.FromProto(response);

                    if (update.IsSnapshot)
                    {
                        Console.WriteLine($"[{DateTime.Now:O}] Received (empty: {update.IsEmptySnapshot}) snapshot for {update.InstrumentId}");

                        if (update.Snapshot.Asks.Any())
                        {
                            Console.WriteLine($"--- Asks ---");
                            Console.WriteLine(string.Join('\n', update.Snapshot.Asks));
                        }

                        if (update.Snapshot.Bids.Any())
                        {
                            Console.WriteLine($"--- Bids ---");
                            Console.WriteLine(string.Join('\n', update.Snapshot.Bids));
                        }
                    }
                    else
                    {
                        Console.WriteLine($"[{DateTime.Now:O}] Received incremental for {update.InstrumentId}");
                        Console.WriteLine($"\t{update.Incremental.Type} - {update.Incremental.Level}");
                    }
                }
            }
        }

        public void Subscribe(int instrumentId) => ProcessSubscription(instrumentId, true);
        public void Unsubscribe(int instrumentId) => ProcessSubscription(instrumentId, false);
        private void ProcessSubscription(int instrumentId, bool subscribe) => _subscriptionChannel.Writer.TryWrite((subscribe, instrumentId));


        public void Dispose()
        {
            _subscriptionChannel.Writer.Complete();
            _shutdownSource.SetResult();
            _streamingTask.GetAwaiter().GetResult();
        }

        private readonly Task _streamingTask = null;
        private readonly TaskCompletionSource _shutdownSource = new TaskCompletionSource();
        private readonly Channel<(bool, int)> _subscriptionChannel = System.Threading.Channels.Channel.CreateUnbounded<(bool, int)>();

    }
}
