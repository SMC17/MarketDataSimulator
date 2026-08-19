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

Files carrying `v2` or `protocolv2` are the current protocol/recovery record. Unsuffixed JSON retains
the earlier full transport sweeps, microstructure study, regenerated controls, and repeatability
runs. Results from different protocol generations are not combined into one claim.

## Reproduce

```bash
dotnet build MarketDataSimulator.sln -c Release

dotnet run --project Bench -c Release --no-build -- \
  protocol --iterations 1000000 --trials 7 --out bench/results/protocol-v2.json

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

A serious capacity study should reserve hosts, pin processes and interrupts, record frequency and
thermal state, separate publishers and consumers, inject controlled loss/reordering, capture
hardware counters, and repeat across x64 and Arm64.
