# Market data dissemination benchmark

Measured capacity of the exchange-to-client broadcast path in this repository:
one simulated matching engine producing order book updates, fanned out over gRPC
duplex streams to many concurrent subscribers.

## Headline

On a 4 vCPU host with the load generator running **on the same box**, the server
sustains the following, with zero dropped updates and every subscriber receiving
the complete feed:

| Operating point | Subscribers | Feed rate | Fan-out | Mean latency | p99 | Runs |
|---|---|---|---|---|---|---|
| **Recommended** - repeatable, comfortable margin | **500** | 100 upd/s | 50,000 msg/s | **11.5 ms** (10.5-12.4) | 43 ms | 4 |
| Most subscribers | **4,000** | 10 upd/s | 40,000 msg/s | **~55 ms** (47-96) | 145 ms | 4 |
| Most throughput | **700** | 100 upd/s | 70,000 msg/s | **31.4 ms** | 138 ms | 1 |
| Round number, large headroom | **1,000** | 10 upd/s | 10,000 msg/s | **23.9 ms** | 47 ms | 1 |

There is no single "how many subscribers" number, because subscriber count and
latency trade off against each other continuously - the full frontier is below.
The 500-subscriber point is the one to quote: it repeats within +/-8% across
runs, whereas the 4,000-subscriber point varies by 2x run to run and the
700-subscriber point sits at 380% of the host's 400% CPU with no margin.

## What is being measured

**Subscriber** - one `StreamOrderbookUpdates` duplex gRPC stream, on its own TCP
connection, subscribed to every instrument on the feed. This is the same call the
`Client` project makes; the harness differs only in that it does not print.

**Update latency** - the interval between the matching engine producing an update
and a subscriber's process receiving it. The server stamps
`Stopwatch.GetTimestamp()` onto the update at the moment of generation, just
before it enters the dissemination path (`Orderbook.PublishUpdateAsync`); the
subscriber subtracts that from its own `Stopwatch.GetTimestamp()` on arrival.

Both processes run on the same host, and on Linux .NET's `Stopwatch` reads
`CLOCK_MONOTONIC`, which is machine-wide. The two readings are therefore directly
comparable with no clock synchronisation and no halving of a round trip. This is
a genuine one-way, end-to-end measurement covering queueing, fan-out,
serialisation, the kernel loopback path and the subscriber's receive path.

Reported latency is the mean over every update delivered to every subscriber
during the measurement window - not per update, and not per subscriber. An update
fanned out to 700 subscribers contributes 700 samples, so subscribers served late
in a fan-out are fully represented.

**Sustained** - a run only counts if all of the following hold:

| Criterion | Why it matters |
|---|---|
| every subscriber stayed connected for the whole run | a run that sheds subscribers is not serving them |
| delivered >= 99% of `subscribers x feed rate` | every subscriber saw the whole feed |
| zero dropped updates | no subscriber overflowed its outbound queue |
| shared update queue stayed bounded | the fan-out kept pace with the matching engine |
| per-subscriber outbound queues stayed bounded | no subscriber accumulated a backlog |

Runs failing these checks are still reported, but marked. A saturated system's
mean latency is a function of how long the run lasted, not of how fast it is.

## Environment

| | |
|---|---|
| CPU | Intel Xeon @ 2.80GHz, 4 vCPU |
| Memory | 15 GB |
| OS | Ubuntu 24.04.4 LTS, kernel 6.18.5 |
| Runtime | .NET 8.0.30 (projects target `net6.0`, run with roll-forward) |
| Build | Release |
| Transport | gRPC over HTTP/2, cleartext, loopback |
| Topology | server and load generator as separate processes on the same host |
| Instruments | 2, depth 10, 5% of updates are full snapshots |
| Per run | 8 s warm-up discarded, 25 s measured |

## Results

### Fan-out throughput axis - 100 updates/s feed

Feed rate held constant, subscriber population increased.

| Subscribers | Fan-out (msg/s) | Mean (ms) | p50 | p99 | p99.9 | Max | Delivered | Server CPU | Host CPU | Sustained |
|---|---|---|---|---|---|---|---|---|---|---|
| 100 | 10,000 | **2.79** | 2.80 | 5.1 | 7.2 | 12.6 | 100.0% | 57% | 66% | yes |
| 200 | 20,001 | **3.52** | 3.25 | 7.9 | 13.7 | 19.3 | 100.0% | 92% | 111% | yes |
| 300 | 30,002 | **6.71** | 6.83 | 13.3 | 19.1 | 25.7 | 100.0% | 124% | 176% | yes |
| 400 | 39,904 | **9.74** | 9.63 | 28.2 | 49.4 | 64.6 | 99.8% | 167% | 257% | yes |
| 500 | 50,012 | **11.37** | 10.35 | 42.8 | 66.7 | 90.3 | 100.0% | 202% | 324% | yes |
| 600 | 59,925 | **17.73** | 13.65 | 80.8 | 117.5 | 171.9 | 99.9% | 221% | 363% | yes |
| 700 | 69,864 | **31.42** | 21.75 | 138.2 | 169.2 | 215.5 | 99.8% | 232% | 380% | yes |
| 800 | 82,172 | 197.32 | 88.45 | 899.0 | 949.0 | 1009.3 | 102.7% | 229% | 390% | **NO** |

At 800 the host is at 390% of 400% and per-subscriber queues pass 100 messages;
delivery above 100% is a backlog draining, not extra data.

### Subscriber count axis - 10 updates/s feed

Population increased on a light feed, so CPU is not the binding constraint until
the very end.

| Subscribers | Fan-out (msg/s) | Mean (ms) | p50 | p99 | p99.9 | Max | Delivered | Server CPU | Host CPU | Sustained |
|---|---|---|---|---|---|---|---|---|---|---|
| 1,000 | 10,000 | **23.92** | 24.25 | 47.4 | 58.5 | 61.4 | 100.0% | 53% | 94% | yes |
| 2,000 | 19,995 | **42.42** | 42.25 | 90.0 | 115.5 | 119.0 | 100.0% | 96% | 176% | yes |
| 3,000 | 29,989 | **57.88** | 55.85 | 136.4 | 169.9 | 174.9 | 100.0% | 123% | 235% | yes |
| 4,000 | 39,998 | **95.64** | 93.75 | 278.6 | 339.9 | 417.9 | 100.0% | 162% | 331% | yes |
| 5,000 | 52,445 | 469.15 | 155.45 | 1695.0 | 1855.0 | 1958.0 | 104.9% | 191% | 376% | **NO** |

Beyond this, at 6,000 subscribers the run loses streams outright: 1,523 of 6,000
subscribers were disconnected mid-run and delivery fell to 83%.

### Before and after the fan-out rework

The same harness against the original dissemination path, which awaited every
subscriber's network write inside the broadcast loop:

| Subscribers | Original mean | Original fan-out | Reworked mean | Reworked fan-out |
|---|---|---|---|---|
| 200 | 82.4 ms | 19,988 msg/s | **3.52 ms** | 20,001 msg/s |
| 300 | 5,240 ms | 21,072 msg/s | **6.71 ms** | 30,002 msg/s |
| 400 | 7,953 ms | 20,520 msg/s | **9.74 ms** | 39,904 msg/s |

The original implementation ceilinged at roughly 20,000 msg/s and collapsed past
~200 subscribers - at 300 and 400 subscribers its fan-out rate is flat at the
ceiling while latency grows without bound, which is the signature of a queue
that never drains. It never exceeded 2 of 4 cores, because the broadcast loop
awaited each subscriber's network write in turn and was itself the serialisation
point. Its best sustained result was 150 subscribers at 8.0 ms.

Raw results for both are in `bench/results/` (`baseline_*` and `feed100_*`).

## Repeatability

Each configuration was run four times (a fresh server process each time) to
separate real capacity from run-to-run noise:

| Point | Runs | Median | Min | Max | Spread |
|---|---|---|---|---|---|
| 500 subscribers, 100 upd/s | 4 | 11.47 ms | 10.53 | 12.38 | 1.17x |
| 4,000 subscribers, 10 upd/s | 4 | 54.59 ms | 47.13 | 95.64 | 2.03x |

Individual means - 500: 11.37, 10.53, 12.38, 11.57 ms. 4,000: 95.64, 47.13,
55.23, 53.95 ms.

The 4,000-subscriber spread is the important caveat. The 95.64 ms figure in the
sweep table above is the slowest of four runs at that configuration; the median
is closer to 55 ms. At that population the run is sensitive to how 4,000
connections happen to land across threads and GC, and one sweep point is not
enough to characterise it. The 500-subscriber point is stable enough to quote
directly.

## What sets the limit

Two different things bound the system, on two different axes.

**Latency is set by subscriber count, not by throughput.** Every update is
written once per subscriber, so a single update's fan-out spans N writes and a
subscriber's latency is essentially its position in that span. Two runs at
identical message rates but different populations show it directly:

| Run | Subscribers | Feed | Fan-out | Mean latency |
|---|---|---|---|---|
| A | 100 | 100 upd/s | 10,000 msg/s | 2.79 ms |
| B | 1,000 | 10 upd/s | 10,000 msg/s | 23.92 ms |

Same work per second; 10x the population costs 8.6x the latency. In run B the
host sat at 94% of 400% - a quarter busy - so this is fan-out span, not CPU
starvation. Subscriber count is bought with latency even on an idle box.

**Throughput is set by CPU cost per message.** At the top of the sustained range
(700 subscribers, 69,864 msg/s, 232% CPU) the server spends roughly **33 us of
CPU per delivered message**. That is what puts the ceiling near 70-80k msg/s once
the co-resident harness takes its share. The dominant term is the per-write trip
through the `Grpc.Core` C-core interop layer; protobuf encoding is no longer
significant, since each update is now encoded once for the whole population
rather than once per subscriber.

## Threats to validity

Read these numbers as "this host, this topology", not as a property of the design.

- **The load generator shares the host.** Near the top of the range the harness
  consumes roughly as much CPU as the server, so the box - not the server - is
  the binding constraint. With subscribers on separate machines the server would
  sustain more. The `Host CPU` column shows the remaining headroom.
- **Loopback, not a network.** No NIC, no switch, no propagation delay. Absolute
  latency on a real network would be higher; the fan-out span would not change.
- **Variance grows with population.** Repeated runs at the same point differ, and
  the spread widens as the host approaches saturation - see Repeatability.
- **One instrument set.** Two instruments at depth 10. Depth changes snapshot
  size and therefore bytes per update.
- **Snapshots are 5% of updates.** Snapshots are far larger than incrementals, so
  this ratio moves the byte rate substantially.
- **File descriptor ceiling.** The container caps a process at 20,000 open files,
  bounding one-connection-per-subscriber runs independently of CPU.
- **The generator is paced on a 1 ms tick**, so feed rates above 1,000 upd/s per
  instrument arrive in small bursts rather than evenly spaced.

## Reproducing

```bash
dotnet build Server/Server.csproj -c Release
dotnet build Bench/Bench.csproj -c Release

# A single operating point
python3 bench/run.py --subscribers 700 --rates 50 --warmup 8 --duration 25 --tag demo

# The two sweeps in this document
python3 bench/run.py --subscribers 100 200 300 400 500 600 700 800 --rates 50 \
    --warmup 8 --duration 25 --tag feed100
python3 bench/run.py --subscribers 1000 2000 3000 4000 5000 --rates 5 \
    --warmup 8 --duration 25 --connect-batch 250 --connect-batch-delay-ms 20 --tag feed10

python3 bench/report.py feed100 feed10
```

`--rates` is per instrument, so `--rates 50` with two instruments is a 100 upd/s
feed. `bench/run.py` starts a fresh server per case, so runs cannot contaminate
each other; raw per-run JSON lands in `bench/results/`.

## Where the remaining headroom is

Not pursued here, in rough order of expected value:

1. **Replace `Grpc.Core` with `Grpc.AspNetCore`.** The C-core binding has been
   unsupported since 2022 and the per-write interop cost is the single largest
   term in the server's CPU budget.
2. **Batch updates per message.** Real feeds carry many updates per packet. This
   protocol sends one message per update, paying the per-write cost every time.
3. **Encode once, write bytes N times.** Encoding is already once per update, but
   each stream re-serialises the message object. A custom marshaller over a
   pre-encoded payload would remove that.
4. **Shard the dissemination loop.** A single reader drains the update queue. Not
   the bottleneck at these rates, but it is a serialisation point.
