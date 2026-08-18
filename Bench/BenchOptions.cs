using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MarketData.Bench
{
    public sealed class BenchOptions
    {
        public string Address { get; private set; } = "http://127.0.0.1:14000";
        public int Subscribers { get; private set; } = 100;
        public int[] Instruments { get; private set; } = new[] { 1, 2 };

        /// <summary>
        /// Subscriber streams multiplexed onto a single HTTP/2 connection. One means every simulated
        /// subscriber gets its own TCP connection, which is the faithful model of independent client
        /// processes; higher values isolate server-side stream fan-out from connection scaling.
        /// </summary>
        public int SubscribersPerConnection { get; private set; } = 1;

        public double WarmupSeconds { get; private set; } = 5;
        public double MeasureSeconds { get; private set; } = 30;

        /// <summary>Subscribers opened per batch during ramp-up, to avoid a connect thundering herd.</summary>
        public int ConnectBatch { get; private set; } = 200;
        public double ConnectBatchDelayMs { get; private set; } = 25;

        public string OutputPath { get; private set; }
        public string Label { get; private set; } = "run";

        public static BenchOptions Parse(string[] args)
        {
            var options = new BenchOptions();

            for (var i = 0; i < args.Length; i++)
            {
                string Next() => i + 1 < args.Length
                    ? args[++i]
                    : throw new ArgumentException($"Missing value for {args[i]}");

                switch (args[i])
                {
                    case "--address": options.Address = Next(); break;
                    case "--subscribers": options.Subscribers = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--instruments": options.Instruments = Next().Split(',').Select(j => int.Parse(j, CultureInfo.InvariantCulture)).ToArray(); break;
                    case "--subscribers-per-connection": options.SubscribersPerConnection = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--warmup": options.WarmupSeconds = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--duration": options.MeasureSeconds = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--connect-batch": options.ConnectBatch = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--connect-batch-delay-ms": options.ConnectBatchDelayMs = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--out": options.OutputPath = Next(); break;
                    case "--label": options.Label = Next(); break;
                    default: throw new ArgumentException($"Unknown argument {args[i]}");
                }
            }

            return options;
        }
    }
}
