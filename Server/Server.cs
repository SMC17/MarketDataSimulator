using MarketData.Common;
using MarketData.Common.Server;
using MarketData.Server;
using MarketData.Server.Configuration;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MarketData.Server
{
    internal class Server : IOrderbookManager, IDisposable
    {
        public Server(ServerConfiguration config)
        {
            _config = config;
            _service = new OrderbookService(config.Port, this, config.VerboseLogging, config.SubscriberQueueCapacity);

            foreach (var instrument in config.Instruments)
                _orderbooks.Add(instrument.Id, new Orderbook(instrument, _service));
        }

        public async Task RunAsync(CancellationToken token)
        {
            await _service.StartAsync().ConfigureAwait(false);

            Console.WriteLine($"Listening on {_config.Port} | instruments: " + string.Join(", ",
                _config.Instruments.Select(i => $"{i.Symbol}(depth {i.Specifications.Depth}, {i.Specifications.UpdatesPerSecond:0.##}/s)")));

            using var lifetime = _config.RunForSeconds > 0
                ? new CancellationTokenSource(TimeSpan.FromSeconds(_config.RunForSeconds))
                : new CancellationTokenSource();

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, token);

            await ReportStatisticsAsync(linked.Token).ConfigureAwait(false);
        }

        private async Task ReportStatisticsAsync(CancellationToken token)
        {
            if (_config.StatisticsIntervalSeconds <= 0)
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }

                return;
            }

            var interval = TimeSpan.FromSeconds(_config.StatisticsIntervalSeconds);
            var previous = _service.GetStatistics();
            var stopwatch = Stopwatch.StartNew();

            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(interval, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                var elapsed = stopwatch.Elapsed.TotalSeconds;
                stopwatch.Restart();

                var current = _service.GetStatistics();

                Console.WriteLine(
                    $"STATS clients={current.ConnectedClients} " +
                    $"queue={current.QueuedUpdates} peakQueue={current.PeakQueuedUpdates} " +
                    $"published/s={(current.PublishedUpdates - previous.PublishedUpdates) / elapsed:0} " +
                    $"disseminated/s={(current.DisseminatedUpdates - previous.DisseminatedUpdates) / elapsed:0} " +
                    $"sent/s={(current.SentMessages - previous.SentMessages) / elapsed:0} " +
                    $"dropped={current.DroppedUpdates} failed={current.FailedSends} " +
                    $"outQueued={current.OutboundQueued} outMax={current.MaxOutboundQueued}");

                previous = current;
            }

            var final = _service.GetStatistics();
            Console.WriteLine($"FINAL published={final.PublishedUpdates} disseminated={final.DisseminatedUpdates} sent={final.SentMessages} dropped={final.DroppedUpdates} failed={final.FailedSends}");
        }

        public OrderbookSnapshotUpdate GetSnapshot(int instrumentId)
        {
            if (!_orderbooks.TryGetValue(instrumentId, out var orderbook))
                throw new InvalidOperationException($"Orderbook ({instrumentId}) does not exist.");

            return orderbook.GetSnapshot();
        }

        public void Dispose()
        {
            foreach (var orderbook in _orderbooks.Values)
                orderbook.Dispose();

            _service.Dispose();
        }

        private readonly ServerConfiguration _config = null;
        private readonly IOrderbookService _service = null;
        private readonly Dictionary<int, Orderbook> _orderbooks = new Dictionary<int, Orderbook>();
    }
}
