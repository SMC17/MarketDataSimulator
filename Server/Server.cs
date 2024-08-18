using MarketData.Common;
using MarketData.Common.Server;
using MarketData.Server;
using MarketData.Server.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MarketData.Server
{
    internal class Server : IOrderbookManager, IDisposable
    {
        public Server(ServerConfiguration config)
        {
            _service = new OrderbookService(config.Port, this);

            foreach (var instrument in config.Instruments)
                _orderbooks.Add(instrument.Id, new Orderbook(instrument, _service));
        }

        public Task RunAsync(CancellationToken token)
            => Task.WhenAll(_service.StartAsync(), Task.Delay(Timeout.Infinite, token));

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

        private readonly IOrderbookService _service = null;
        private readonly Dictionary<int, Orderbook> _orderbooks = new Dictionary<int, Orderbook>();
    }
}
