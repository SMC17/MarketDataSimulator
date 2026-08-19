using Grpc.Core;
using Grpc.Net.Client;
using MarketData.Bench;
using Proto;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

// Two suites live in this harness: the default dissemination benchmark, and a micro-benchmark
// of the order book implementations themselves.
// Environment.Exit rather than return: the dissemination suite ends with lingering stream
// readers, and both suites exit the same way so the harness has one shutdown path.
if (args.Length > 0 && args[0] == "books")
    Environment.Exit(BookBenchmark.Run(args.Skip(1).ToArray()));

if (args.Length > 0 && args[0] == "matching")
    Environment.Exit(MatchingBenchmark.Run(args.Skip(1).ToArray()));

if (args.Length > 0 && args[0] == "multicast")
    Environment.Exit(MulticastBenchmark.Run(args.Skip(1).ToArray()));

var options = BenchOptions.Parse(args);

// Client and server share this host's monotonic clock, so the server's Stopwatch timestamp can be
// subtracted from the subscriber's directly - no clock synchronisation and no round-trip halving.
Console.WriteLine($"Stopwatch: highResolution={Stopwatch.IsHighResolution} frequency={Stopwatch.Frequency}");
Console.WriteLine($"Subscribers={options.Subscribers} perConnection={options.SubscribersPerConnection} " +
                  $"instruments=[{string.Join(",", options.Instruments)}] warmup={options.WarmupSeconds}s measure={options.MeasureSeconds}s");

var histogram = new LatencyHistogram(32);
var connectedSubscribers = 0;
var failedSubscribers = 0;
long receivedDuringMeasurement = 0;
long receivedTotal = 0;
long firstUpdateFailures = 0;

var measuring = 0;
var shuttingDown = 0;
using var lifetime = new CancellationTokenSource();

var connectionCount = (int)Math.Ceiling(options.Subscribers / (double)options.SubscribersPerConnection);
var channels = new List<GrpcChannel>(connectionCount);

for (var i = 0; i < connectionCount; i++)
{
    channels.Add(GrpcChannel.ForAddress(options.Address, new GrpcChannelOptions
    {
        HttpHandler = new SocketsHttpHandler
        {
            EnableMultipleHttp2Connections = false,
            PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
            KeepAlivePingDelay = TimeSpan.FromSeconds(30),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(10),
        },
        // Every byte the harness spends decoding is a byte of CPU not available to the server on
        // this shared host, so keep the client path as thin as the protocol allows.
        MaxReceiveMessageSize = 4 * 1024 * 1024,
        ThrowOperationCanceledOnCancellation = true,
    }));
}

var subscriberTasks = new List<Task>(options.Subscribers);
var readySignal = new CountdownEvent(options.Subscribers);
var rampStopwatch = Stopwatch.StartNew();

for (var i = 0; i < options.Subscribers; i++)
{
    var index = i;
    var channel = channels[index / options.SubscribersPerConnection];

    subscriberTasks.Add(Task.Run(() => RunSubscriberAsync(index, channel)));

    if ((index + 1) % options.ConnectBatch == 0 && options.ConnectBatchDelayMs > 0)
        await Task.Delay(TimeSpan.FromMilliseconds(options.ConnectBatchDelayMs)).ConfigureAwait(false);
}

readySignal.Wait(TimeSpan.FromSeconds(120));
Console.WriteLine($"Ramp-up complete in {rampStopwatch.Elapsed.TotalSeconds:0.0}s: " +
                  $"connected={Volatile.Read(ref connectedSubscribers)} failed={Volatile.Read(ref failedSubscribers)} " +
                  $"over {connectionCount} connection(s)");

await Task.Delay(TimeSpan.FromSeconds(options.WarmupSeconds)).ConfigureAwait(false);

var processBefore = SampleProcess();
Interlocked.Exchange(ref measuring, 1);
var measureStopwatch = Stopwatch.StartNew();

await Task.Delay(TimeSpan.FromSeconds(options.MeasureSeconds)).ConfigureAwait(false);

Interlocked.Exchange(ref measuring, 0);
var measuredSeconds = measureStopwatch.Elapsed.TotalSeconds;
var processAfter = SampleProcess();

var summary = histogram.Summarise(50, 90, 99, 99.9, 99.99);
var messages = Interlocked.Read(ref receivedDuringMeasurement);

// Streams torn down at end of run raise transport errors that are not subscriber losses.
Interlocked.Exchange(ref shuttingDown, 1);
lifetime.Cancel();

var report = new BenchReport(
    options.Label,
    DateTimeOffset.UtcNow,
    options.Subscribers,
    Volatile.Read(ref connectedSubscribers),
    Volatile.Read(ref failedSubscribers),
    connectionCount,
    options.Instruments,
    Math.Round(measuredSeconds, 3),
    messages,
    Math.Round(messages / measuredSeconds, 1),
    Math.Round(messages / measuredSeconds / Math.Max(1, Volatile.Read(ref connectedSubscribers)), 2),
    Math.Round(summary.MeanMs, 3),
    Math.Round(summary.MinMs, 3),
    Math.Round(summary.At(50), 3),
    Math.Round(summary.At(90), 3),
    Math.Round(summary.At(99), 3),
    Math.Round(summary.At(99.9), 3),
    Math.Round(summary.At(99.99), 3),
    Math.Round(summary.MaxMs, 3),
    Math.Round((processAfter.Cpu - processBefore.Cpu).TotalSeconds / measuredSeconds * 100, 1),
    Math.Round(processAfter.WorkingSetMb, 1),
    Interlocked.Read(ref firstUpdateFailures));

Console.WriteLine();
Console.WriteLine($"RESULT subscribers={report.ConnectedSubscribers}/{report.RequestedSubscribers} " +
                  $"msgs={report.MessagesReceived} throughput={report.MessagesPerSecond:N0}/s " +
                  $"perSubscriber={report.MessagesPerSecondPerSubscriber:0.##}/s");
Console.WriteLine($"RESULT latency ms: mean={report.MeanMs:0.###} min={report.MinMs:0.###} p50={report.P50Ms:0.###} " +
                  $"p90={report.P90Ms:0.###} p99={report.P99Ms:0.###} p99.9={report.P999Ms:0.###} max={report.MaxMs:0.###}");
Console.WriteLine($"RESULT harness cpu={report.HarnessCpuPercent:0.#}% rssMb={report.HarnessWorkingSetMb:0.#}");

if (options.OutputPath is not null)
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.OutputPath)));
    File.WriteAllText(options.OutputPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"Wrote {options.OutputPath}");
}

Environment.Exit(0);

async Task RunSubscriberAsync(int index, GrpcChannel channel)
{
    var signalled = false;

    try
    {
        var client = new Proto.OrderbookService.OrderbookServiceClient(channel);

        using var call = client.StreamOrderbookUpdates(cancellationToken: lifetime.Token);

        var subscription = new Subscription { Subscribe = new SubscribeRequest() };
        subscription.Subscribe.Ids.AddRange(options.Instruments);

        await call.RequestStream.WriteAsync(subscription).ConfigureAwait(false);

        Interlocked.Increment(ref connectedSubscribers);
        readySignal.Signal();
        signalled = true;

        await foreach (var update in call.ResponseStream.ReadAllAsync(lifetime.Token).ConfigureAwait(false))
        {
            var now = Stopwatch.GetTimestamp();

            Interlocked.Increment(ref receivedTotal);

            if (Volatile.Read(ref measuring) == 0)
                continue;

            if (update.SourceTimestamp == 0)
            {
                Interlocked.Increment(ref firstUpdateFailures);
                continue;
            }

            histogram.Record(index, LatencyHistogram.ToMicroseconds(now - update.SourceTimestamp));
            Interlocked.Increment(ref receivedDuringMeasurement);
        }
    }
    catch (Exception e) when (e is OperationCanceledException || (e as RpcException)?.StatusCode == StatusCode.Cancelled)
    {
        // Expected: the run ended and the streams were torn down.
    }
    catch (Exception e)
    {
        if (Volatile.Read(ref shuttingDown) == 1)
            return;

        if (Interlocked.Increment(ref failedSubscribers) <= 5)
            Console.WriteLine($"Subscriber {index} failed: {e.GetType().Name}: {e.Message}");
    }
    finally
    {
        if (!signalled)
            readySignal.Signal();
    }
}

static (TimeSpan Cpu, double WorkingSetMb) SampleProcess()
{
    using var process = Process.GetCurrentProcess();
    process.Refresh();
    return (process.TotalProcessorTime, process.WorkingSet64 / 1024.0 / 1024.0);
}

record BenchReport(
    string Label,
    DateTimeOffset TimestampUtc,
    int RequestedSubscribers,
    int ConnectedSubscribers,
    int FailedSubscribers,
    int Connections,
    int[] Instruments,
    double MeasuredSeconds,
    long MessagesReceived,
    double MessagesPerSecond,
    double MessagesPerSecondPerSubscriber,
    double MeanMs,
    double MinMs,
    double P50Ms,
    double P90Ms,
    double P99Ms,
    double P999Ms,
    double P9999Ms,
    double MaxMs,
    double HarnessCpuPercent,
    double HarnessWorkingSetMb,
    long UnstampedUpdates);
