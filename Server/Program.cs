using MarketData.Server;
using MarketData.Server.Configuration;

using (var server = new Server(ServerConfiguration.FromAppSettings()))
    await server.RunAsync(default).ConfigureAwait(false);
