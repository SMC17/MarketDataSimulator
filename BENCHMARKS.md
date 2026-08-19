# Benchmark record

This document states what was measured, how it was measured, and what it does not prove. Generated
regions are checked against committed JSON in CI.

## Environment

Measurements were recorded on 2026-08-19.

<!-- generated: v2-environment -->
| Property | Recorded value |
|---|---|
| Runtime | .NET 8.0.30, Release, Server GC |
| OS / architecture | Ubuntu 24.04.3 LTS, X64 |
| Logical processors | 8 |
| Monotonic clock | 1000 MHz |
| CRC-32C | SSE4.2 (hardware) |
<!-- /generated -->

The host is virtualized and shared. CPU frequency, core isolation, interrupt routing, NUMA placement,
and NIC hardware were not controlled. Results are useful for regression and design comparison on
this host; they are not hardware-independent capacity claims.

## Method

- Harnesses perform untimed warm-up and report the median of at least five trials. Min and max stay
  in JSON to expose noise.
- Matching cases are state-preserving two-command cycles. Setup and generated scripts sit outside
  the timed region; resting population does not drift.
- Book sweeps discard one full pass before recording, allowing tiered JIT promotion and shared
  interface call sites to stabilize across implementations.
- Allocation uses `GC.GetAllocatedBytesForCurrentThread` on single-threaded paths and
  `GC.GetTotalAllocatedBytes` on concurrent queue paths.
- Packet bytes and checksums are consumed so dead-code elimination cannot remove protocol work.
- WAL timings stop at the policy's acknowledgement point; final disposal sync is outside the timed
  region. Range and recovery trials are warm-cache filesystem measurements.
- Transport load is open-loop. Source timestamps precede dissemination, so backlog increases
  measured latency instead of reducing offered load.
- CI runs smoke-sized benchmarks for rot detection; it does not gate performance on shared runners.

The harness is dependency-free rather than BenchmarkDotNet-based. It does not provide process
isolation, CPU affinity, frequency stabilization, hardware counters, or overhead subtraction. Raw
ranges are therefore part of the result.

## Protocol v2

Artifact: `bench/results/protocol-v2.json`. One million base iterations, seven trials.

<!-- generated: v2-protocol -->
| Case | Packet | Median | Min–max | Rate | Allocation |
|---|---|---|---|---|---|
| seal incremental | 50 B | 27.7 ns | 26.2–72.7 ns | 36.15 M/s | 0 B/op |
| validate incremental | 50 B | 18.6 ns | 18.4–21.2 ns | 53.65 M/s | 0 B/op |
| validate max batch | 1,394 B | 579.8 ns | 572.9–822.7 ns | 1.73 M/s | 0 B/op |
| validate max snapshot | 1,395 B | 204.1 ns | 203.2–230.0 ns | 4.90 M/s | 0 B/op |
| encode + decode + apply | 50 B | 121.8 ns | 121.1–354.3 ns | 8.21 M/s | 0 B/op |
<!-- /generated -->

Batch validation scans 97 message boundaries; snapshot validation scans one large message. The
end-to-end case includes seal, CRC validation, decoder locking, bounded reorder and A/B identity
retention, sequencing, and in-place depth application. CRC correctness is checked against the
standard `123456789` vector. Corruption tests assert that state and sequence do not advance.

## Durable publication and recovery

Artifact: `bench/results/durability-v2.json`. Five trials; 5,000 append acknowledgements per trial,
50,000-message recovery log, and 100 ten-message range requests. The JSON embeds runtime and host
metadata.

<!-- generated: v2-durability -->
| Append contract | Policy | Payload | Median | Min–max | Rate | Syncs/trial | Allocation |
|---|---|---|---|---|---|---|---|
| OS page cache | OsBuffered | 64 B | 976.7 ns | 958.0–2,088.2 ns | 1,023,815/s | 0 | 0 B/op |
| periodic 1 ms | SyncPeriodic | 64 B | 2,318.3 ns | 2,149.5–3,507.9 ns | 431,343/s | 4 | 0 B/op |
| group commit 64 | OsBuffered | 64 B | 27,301.7 ns | 17,837.9–42,412.7 ns | 36,628/s | 79 | 0 B/op |
| fsync each | SyncEachRecord | 64 B | 972,646.2 ns | 898,242.4–1,196,500.7 ns | 1,028/s | 5,000 | 0 B/op |
| seal + packet WAL | OsBuffered | 50 B | 813.7 ns | 755.1–1,676.6 ns | 1,228,894/s | 0 | 0 B/op |

| Messages | Checkpoint | Full replay | Checkpoint + tail | Speed-up |
|---|---|---|---|---|
| 50,000 | 47,500 | 24.68 ms (21.00–40.83) | 2.92 ms (2.64–3.84) | 8.45× |

| 10-message range | Queries | Index entries | Median | Min–max | Allocation |
|---|---|---|---|---|---|
| sparse index | 100 | 196 | 73.6 µs | 70.2–81.7 µs | 1,736 B/request |
| segment scan | 100 | 0 | 1,946.8 µs | 927.1–2,530.3 µs | 5,848 B/request |
<!-- /generated -->

`OS page cache` includes framing, CRC-32C, and one unbuffered managed write into the kernel cache;
it is not power-loss durability. `seal + packet WAL` also seals and validates the 50-byte feed
packet. The 1 ms periodic case stresses group sync; the configurable server default is 200 ms.
`fsync each` measures this virtual disk, not a portable storage latency.

Recovery trials alternate full-first and checkpoint-first order. The checkpoint is at sequence
47,500; complete segments before it are skipped. The sparse range index stores one entry per 256
records and incrementally follows the live tail. Both range cases copy the same ten payloads; the
table isolates lookup strategy.

## Matching engine

Artifact: `bench/results/matching-v2.json`. Each value is the median over 200,000 state-preserving
cycles and five trials.

<!-- generated: v2-matching -->
| Resting orders | Add + cancel | Cancel + add | Match + replenish |
|---|---|---|---|
| 100 | 91.5 ns | 102.4 ns | 88.7 ns |
| 1,000 | 104.7 ns | 75.7 ns | 86.0 ns |
| 10,000 | 84.7 ns | 81.8 ns | 88.6 ns |
| 100,000 | 72.6 ns | 117.1 ns | 98.7 ns |
<!-- /generated -->

Each cycle contains two engine commands. Match-plus-replenish uses exact 50-share removals and
same-price replacements. These values are not the latency of an isolated exchange command. Growth
with population can reflect cache and TLB pressure even when the algorithmic operation count stays
constant; hardware counters would be required to attribute it.

## Aggregated books

Artifact: `bench/results/books-v2.json`. Every implementation sees the same seeded 200,000-operation
stream. Setup is outside the timed region.

<!-- generated: v2-books -->
| Depth | Implementation | Mixed ns/op | Touch ns/op | Top-10 ns/op | Clear ns/op | Publish B/op |
|---|---|---|---|---|---|---|
| 10 | SortedArrayBook | 33.1 | 2.6 | 8.2 | 29.9 | 0 |
| 10 | VectorizedBook | 25.2 | 6.1 | 19.3 | 39.2 | 0 |
| 10 | LadderBook | 25.6 | 7.2 | 52.7 | 134.5 | 0 |
| 10 | TreeBook | 54.6 | 12.8 | 353.2 | 63.3 | 104 |
| 100 | SortedArrayBook | 47.8 | 2.7 | 10.2 | 30.2 | 0 |
| 100 | VectorizedBook | 37.2 | 6.1 | 20.3 | 50.2 | 0 |
| 100 | LadderBook | 27.6 | 7.3 | 52.8 | 1149.5 | 0 |
| 100 | TreeBook | 89.5 | 15.1 | 224.3 | 157.8 | 152 |
| 1,000 | SortedArrayBook | 64.5 | 2.5 | 7.4 | 32.6 | 0 |
| 1,000 | VectorizedBook | 53.8 | 6.0 | 19.6 | 227.6 | 0 |
| 1,000 | LadderBook | 35.9 | 7.5 | 53.0 | 12052.4 | 0 |
| 1,000 | TreeBook | 123.9 | 18.4 | 304.9 | 983.4 | 200 |
<!-- /generated -->

The ranking changes with depth: shifting contiguous arrays is competitive shallow, while direct
price indexing wins deeper update workloads. Tree enumeration allocates a traversal stack and is
kept as a contrasting structure, not the zero-allocation production publish path.

## Queue hand-off

Artifact: `bench/results/queue-v2.json`. One producer, one consumer, one million items, capacity
8,192, seven trials. Concurrent cases busy-spin.

<!-- generated: v2-queue -->
| Queue | Median | Min–max | Throughput | Allocation |
|---|---|---|---|---|
| RingBuffer (single thread) | 3.5 ns/item | 3.5–4.1 | 285.7 M item/s | 0.066 B/item |
| Channel (single thread) | 69.5 ns/item | 61.8–109.7 | 14.4 M item/s | 0.001 B/item |
| RingBuffer (producer + consumer) | 14.3 ns/item | 11.3–27.2 | 69.8 M item/s | 0.066 B/item |
| RingBuffer batched (prod + cons) | 6.3 ns/item | 5.8–7.2 | 158.5 M item/s | 0.066 B/item |
| Channel (producer + consumer) | 163.8 ns/item | 100.3–273.9 | 6.1 M item/s | 0.133 B/item |
<!-- /generated -->

The small ring allocation is fixed task/harness setup divided by the item count; ring operations are
allocation-free. Busy-spin throughput is not an energy, fairness, or end-to-end latency result.

## Real market data

Artifacts: `replay-sample-v2-AMZN.json` and `replay-sample-v2-MSFT.json`. Five trials; throughput is
the median. Committed gzip files are checksum-verified before tests.

<!-- generated: v2-replay -->
| Symbol | Depth | Book | Transitions | Exact | Accuracy | Msg/s |
|---|---|---|---|---|---|---|
| AMZN | 10 | SortedArray | 19,999 | 19,999 | 100.0000% | 589,186 |
| AMZN | 10 | Vectorized | 19,999 | 19,999 | 100.0000% | 479,542 |
| AMZN | 10 | Ladder | 19,999 | 19,999 | 100.0000% | 127,975 |
| AMZN | 10 | Tree | 19,999 | 19,999 | 100.0000% | 115,348 |
| MSFT | 5 | SortedArray | 19,999 | 19,999 | 100.0000% | 1,089,271 |
| MSFT | 5 | Vectorized | 19,999 | 19,999 | 100.0000% | 1,326,013 |
| MSFT | 5 | Ladder | 19,999 | 19,999 | 100.0000% | 581,543 |
| MSFT | 5 | Tree | 19,999 | 19,999 | 100.0000% | 343,020 |
<!-- /generated -->

Transition replay seeds from the published book, applies one event, and compares the determined
prefix with the exchange's next row. This is exact where a finite-depth source is observable.
Cumulative replay is a separate observability experiment: hidden liquidity below depth N cannot be
reconstructed from a depth-N history.

Full-session artifacts cover 269,747 AMZN, 112,672 GOOG, and 595,799 MSFT transitions: 978,218 exact
transitions per implementation. Those artifacts predate transport v2 but exercise the same
book/replay layer.

## Protocol-v2 multicast

Artifact: `bench/results/protocolv2-summary.json`. Two instruments target 1,000 aggregate updates/s,
batch limit 16, 1 ms partial-batch flush, 4 s warm-up, and 10 s measurement.

<!-- generated: v2-multicast -->
| Subscribers | Delivered msg/s | Per subscriber | Mean | p50 | p99 | Gaps / CRC / divergence / stale |
|---|---|---|---|---|---|---|
| 100 | 97,028 | 970 | 0.620 ms | 0.366 ms | 6.942 ms | 0 / 0 / 0 / 0 |
| 500 | 485,393 | 971 | 1.663 ms | 1.070 ms | 12.150 ms | 0 / 0 / 0 / 0 |
<!-- /generated -->

Loopback still copies each datagram into every local socket. A switched multicast network performs
replication elsewhere; these runs do not model switch queues, NIC rings, packet loss, or propagation.
The capacity run uses one line. Integration smoke tests exercise A/B publication and duplicate
arbitration with zero sequence gaps.

## Transport scaling (pre-v2 generation)

These runs predate protocol v2 and use a separate 4-vCPU host. Compare rows within this section;
do not compare them with v2 results.

<!-- generated: environment -->
|  |  |
|---|---|
| CPU | Intel(R) Xeon(R) Processor @ 2.80GHz, 4 vCPU |
| CPU features | avx2, avx512f, avx512bw, avx512dq, avx512vl, bmi1, bmi2, popcnt |
| Memory | 15.7 GB |
| OS | Ubuntu 24.04.4 LTS, kernel 6.18.5-fc-v20 |
| Runtime | .NET 8.0.30, all projects target `net8.0` |
| Build | Release, Server GC |
| Topology | server and load generator as separate processes on the same host |
<!-- /generated -->

### Unicast fan-out

TCP fan-out performs one write per subscriber per update. These runs have similar delivered rates
but different subscriber counts:

<!-- generated: equal-work -->
| Subscribers | Feed rate | Fan-out | Mean latency |
|---|---|---|---|
| 100 | 100 upd/s | 9,490 msg/s | **1.61 ms** |
| 1,000 | 10 upd/s | 10,100 msg/s | **18.63 ms** |
<!-- /generated -->

At approximately 10,000 messages/s, increasing the audience from 100 to 1,000 subscribers raised
mean latency from 1.61 ms to 18.63 ms.

The full sweep, feed rate held at 100 updates/s aggregate:

<!-- generated: unicast-sweep -->
| Subscribers | Fan-out (msg/s) | Mean (ms) | p50 | p99 | p99.9 | Max | Delivered | Gen. rate | Server CPU | Host CPU | Sustained |
|---|---|---|---|---|---|---|---|---|---|---|---|
| 100 | 9,490 | **1.61** | 1.38 | 5.8 | 26.1 | 28.4 | 99.1% | 96% | 52.8% | 18.2% | yes |
| 200 | 19,319 | **4.19** | 4.11 | 8.2 | 18.9 | 26.1 | 99.4% | 97% | 77.8% | 92.6% | yes |
| 300 | 29,253 | **4.13** | 3.76 | 10.3 | 18.4 | 49.8 | 99.6% | 98% | 107.7% | 141.2% | yes |
| 400 | 38,294 | **7.35** | 7.44 | 15.4 | 28.9 | 41.9 | 99.0% | 97% | 131.4% | 186.4% | yes |
| 500 | 48,117 | **8.54** | 8.04 | 28.4 | 62.6 | 97.4 | 99.2% | 97% | 160.9% | 242.4% | yes |
| 600 | 57,798 | **9.71** | 9.28 | 25.1 | 51.1 | 55.1 | 99.4% | 97% | 188.4% | 286.9% | yes |
| 700 | 67,385 | **16.35** | 14.05 | 80.5 | 103.0 | 131.9 | 99.6% | 97% | 216.3% | 344.0% | yes |
| 800 | 77,760 | **18.73** | 15.05 | 83.8 | 133.2 | 181.2 | 99.5% | 98% | 216.3% | 355.6% | yes |
| 900 | 86,621 | **46.15** | 23.75 | 216.7 | 254.2 | 322.8 | 99.2% | 97% | 229.4% | 378.8% | yes |
<!-- /generated -->

Every point sustained; 900 subscribers is the top of the sweep, not a measured limit. Server CPU
rose from 52.8% to 229.4%; p99 reached 216.7 ms at 900 subscribers.

A second sweep holds the message rate constant on a lighter feed:

<!-- generated: unicast-sweep-light -->
| Subscribers | Fan-out (msg/s) | Mean (ms) | p50 | p99 | p99.9 | Max | Delivered | Gen. rate | Server CPU | Host CPU | Sustained |
|---|---|---|---|---|---|---|---|---|---|---|---|
| 1,000 | 10,100 | **18.63** | 18.05 | 50.0 | 61.1 | 61.6 | 100.0% | 101% | 42.8% | 65.1% | yes |
| 2,000 | 19,868 | **34.97** | 35.15 | 77.3 | 137.2 | 137.9 | 103.5% | 96% | 68.7% | 121.1% | yes |
| 3,000 | 30,100 | **55.35** | 49.95 | 223.2 | 264.4 | 291.9 | 100.3% | 100% | 108.9% | 198.3% | yes |
| 4,000 | 39,998 | **69.22** | 65.25 | 180.2 | 289.6 | 357.2 | 100.0% | 100% | 134.1% | 253.9% | yes |
| 5,000 | 50,334 | **96.83** | 87.95 | 321.1 | 387.1 | 422.2 | 100.7% | 100% | 157.4% | 304.2% | yes |
<!-- /generated -->

### Multicast fan-out

The publisher encodes each update once and sends a single datagram; the network performs the
replication.

<!-- generated: multicast-sweep -->
| Subscribers | Fan-out (msg/s) | Mean (ms) | p50 | p99 | Max | Delivered | Server pkt/s | Gaps | Stale | Server CPU | Host CPU | Sustained |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 100 | 9,906 | **0.31** | 0.28 | 0.9 | 6.0 | 99.4% | 99.7 | 0 | 0 | 13.8% | 12.7% | yes |
| 250 | 24,762 | **0.54** | 0.50 | 1.3 | 23.1 | 99.5% | 99.5 | 0 | 0 | 16.5% | 16.0% | yes |
| 500 | 49,524 | **0.85** | 0.81 | 2.1 | 5.3 | 99.4% | 99.6 | 0 | 0 | 19.0% | 15.8% | yes |
| 1,000 | 97,499 | **1.68** | 1.63 | 4.0 | 9.6 | 98.9% | 98.6 | 0 | 0 | 25.8% | 47.4% | yes |
| 2,000 | 199,895 | **3.22** | 2.99 | 8.5 | 24.0 | 99.6% | 100.3 | 0 | 0 | 30.9% | 166.3% | yes |
| 4,000 | 392,451 | **11.13** | 9.95 | 35.6 | 127.9 | 99.2% | 98.9 | 0 | 0 | 49.0% | 358.7% | yes |
| 6,000 | 594,067 | **34.39** | 23.75 | 184.7 | 470.3 | 99.9% | 99.1 | 0 | 0 | 73.2% | 372.5% | yes |
| 8,000 | 594,357 | **744.52** | 350.45 | 10365.0 | 24032.8 | 91.2% | 81.5 | 594 | 0 | 91.6% | 373.9% | **NO** |
<!-- /generated -->

Server packet rate stayed between 98.6 and 100.3/s through 6,000 subscribers. The 8,000-subscriber
run was not sustained and recorded 594 sequence gaps.

<!-- generated: head-to-head -->
| Subscribers | Unicast mean | Multicast mean | Improvement |
|---|---|---|---|
| 100 | 1.61 ms | **0.31 ms** | **5.3×** |
| 500 | 8.54 ms | **0.85 ms** | **10.1×** |
<!-- /generated -->

Cost per delivered message, at each transport's highest sustained point:

<!-- generated: cost-per-message -->
| Transport | Highest sustained subscribers | Messages/s | Server CPU | Server CPU per message |
|---|---|---|---|---|
| Unicast gRPC | 900 | 86,621 | 229.4% | **26.48 µs** |
| Multicast | 6,000 | 594,067 | 73.2% | **1.23 µs** |
<!-- /generated -->

The next multicast point failed; no unicast failure point was measured.

### Batching

The following runs use 1,000 subscribers and 1,000 aggregate updates/s while varying packet batch
size.

<!-- generated: batching -->
| Max batch | Fan-out (msg/s) | Mean (ms) | p99 | Server pkt/s | Server CPU | Host CPU |
|---|---|---|---|---|---|---|
| 1 | 866,035 | **3.32** | 14.3 | 890.0 | 78.1% | 377.7% |
| 4 | 971,437 | **2.25** | 5.6 | 299.6 | 51.2% | 244.5% |
| 16 | 972,472 | **2.18** | 5.2 | 199.9 | 46.7% | 172.3% |
| 64 | 971,792 | **2.09** | 4.7 | 201.0 | 45.0% | 170.9% |
<!-- /generated -->

Batch 64 versus batch 1 reduced packet rate 4.4×, mean latency 1.6×, p99 3.1×, and host CPU 2.2×;
delivered throughput increased 12%.

### Repeatability

<!-- generated: repeatability -->
| Point | Runs | Median | Min | Max | Spread |
|---|---|---|---|---|---|
| 4,000 subscribers, 10 upd/s | 3 | 47.18 ms | 43.25 | 60.35 | 1.40× |
| 500 subscribers, 100 upd/s | 3 | 8.64 ms | 7.73 | 8.66 | 1.12× |
<!-- /generated -->

The 4,000-subscriber configuration varied 1.4× across three runs. Sweep rows are single runs.

## Market realism

Exact book reconstruction does not make generated order flow realistic. Mid-price returns sampled
every 20 updates provide a blunt distributional check:

<!-- generated: realism -->
| Series | Observations | Excess kurtosis | \|r\| ac(1) | \|r\| ac(10) | >3σ | >5σ |
|---|---|---|---|---|---|---|
| AMZN (real) | 13,487 | 23.49 | 0.1793 | 0.1149 | 1.690% | 0.289% |
| GOOG (real) | 5,633 | 24.20 | 0.2262 | 0.0915 | 1.207% | 0.373% |
| MSFT (real) | 29,789 | 14.53 | 0.0253 | 0.0391 | 0.940% | 0.940% |
| simulator | 19,999 | 7,969.82 | 0.2244 | 0.2808 | 0.620% | 0.055% |
<!-- /generated -->

The real sessions show fat tails and persistent absolute-return autocorrelation. The simulator's
extreme kurtosis with lower tail counts describes a nearly static price punctuated by rare jumps,
not a market-like return distribution. The simulator is therefore a systems load generator, not a
market model. Distribution-dependent studies use real data.

## Artifact boundary

Files carrying `v2` or `protocolv2` are the current protocol/recovery record. Unsuffixed JSON holds
the earlier transport sweeps, microstructure study, regenerated controls, and repeatability runs,
presented under [Transport scaling](#transport-scaling-pre-v2-generation).

The generations use different hosts and are not compared. `docgen.py` rejects mixed kernel
instances within either generation.

## Reproduce

```bash
dotnet build MarketDataSimulator.sln -c Release

dotnet run --project Bench -c Release --no-build -- \
  protocol --iterations 1000000 --trials 7 --out bench/results/protocol-v2.json

dotnet run --project Bench -c Release --no-build -- \
  durability --records 5000 --payload 64 --trials 5 --range-queries 100 \
  --out bench/results/durability-v2.json

dotnet run --project Bench -c Release --no-build -- \
  queue --items 1000000 --capacity 8192 --trials 7 --out bench/results/queue-v2.json

dotnet run --project Bench -c Release --no-build -- \
  matching --sizes 100,1000,10000,100000 --out bench/results/matching-v2.json

dotnet run --project Bench -c Release --no-build -- \
  replay --data data/sample --trials 5 --out bench/results/replay-sample-v2.json

python3 bench/run_multicast.py --subscribers 100 500 --rates 500 \
  --instruments 2 --warmup 4 --duration 10 --max-batch 16 \
  --flush-interval-ms 1 --tag protocolv2

python3 bench/docgen.py --write
python3 bench/docgen.py --check
```

Refresh the complete pre-v2 generation together; mixed-host partial refreshes are rejected.

```bash
python3 bench/environment.py

python3 bench/run.py --tag scale50 --rates 50 --subscribers 100 200 300 400 500 600 700 800 900
python3 bench/run.py --tag scale5  --rates 5  --subscribers 1000 2000 3000 4000 5000

python3 bench/run_multicast.py --tag mcast --rates 50 \
  --subscribers 100 250 500 1000 2000 4000 6000 8000

for b in 1 4 16 64; do
  python3 bench/run_multicast.py --tag batch$b --max-batch $b --flush-interval-ms 1 \
    --rates 500 --subscribers 1000
done

for i in 1 2 3; do
  python3 bench/run.py --tag qchannel$i --rates 50 --subscribers 500
  python3 bench/run.py --tag qring$i --ring --rates 50 --subscribers 500
  python3 bench/run.py --tag rep$i --rates 5 --subscribers 4000
done
```

A capacity study requires reserved hosts, process and interrupt affinity, frequency and thermal
telemetry, separate publishers and consumers, controlled loss/reordering, hardware counters, and
x64/Arm64 runs.
