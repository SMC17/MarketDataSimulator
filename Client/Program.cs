using Grpc.Net.Client;
using MarketData.Common.Client;

var channel = GrpcChannel.ForAddress("http://localhost:14000");
var client = new Client(channel);

Console.WriteLine("Enter 'Subscribe' or 'Unsubscribe' followed by the InstrumentId.");

while (true)
{
    var line = Console.ReadLine();

    if (string.IsNullOrWhiteSpace(line))
        return;

    var splitLine = line.Split(' ');

    if (splitLine.Length is not 2)
    {
        Console.WriteLine("Invalid input.");
        continue;
    }

    var isInstrumentId = int.TryParse(splitLine[^1], out var instrumentId);

    if (!isInstrumentId)
    {
        Console.WriteLine($"Unknown second argument.");
        continue;
    }

    if (splitLine[0] is "Subscribe")
    {
        client.Subscribe(instrumentId);
    }
    else if (splitLine[0] is "Unsubscribe")
    {
        client.Unsubscribe(instrumentId);
    }
    else
    {
        Console.WriteLine($"Unknown first argument.");
        continue;
    }
}