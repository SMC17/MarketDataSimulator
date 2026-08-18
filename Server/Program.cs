using MarketData.Server;
using MarketData.Server.Configuration;

var configuration = ServerConfiguration.FromJson(args.FirstOrDefault() ?? "appsettings.json");

using (var server = new Server(configuration))
    await server.RunAsync(default).ConfigureAwait(false);
