# MarketDataSimulator

A deterministic exchange core and market-data recovery laboratory for .NET 8. The system is built
around falsifiable invariants: price-time priority, exact replay, explicit stale state, bounded
memory, wire integrity, and measured allocation.

This is a research system, not a production venue. The implemented boundary is explicit; see
[Production boundary](#production-boundary).

## System

| Layer | Implementation |
|---|---|
| Matching | Single-writer price-time book; limit/market, GTC/IOC/FOK, cancel, reduce |
| Price discovery | Price-indexed occupancy bitset with hardware bit scans |
| Order lookup | Hash index plus intrusive FIFO queues per price |
| Public depth | Derived from matching events; no second source of truth |
| Dissemination | Bounded gRPC fan-out or sequenced UDP multicast with A/B arbitration |
| Wire protocol | Fixed little-endian v2 packets, session identity, exact length, CRC-32C |
| Durability | Single-writer segmented WAL, restart repair, CRC-checked checkpoints |
| Recovery | Bounded reorder, exact TCP gap fill, sparse range index, atomic snapshots |
| Validation | Differential/property tests and LOBSTER-derived NASDAQ replay |
| Analytics | Allocation-free order-flow imbalance, online regression, stylized facts |

The matching engine is single-writer by design. Concurrency sits before and after sequencing, not
inside the structure that defines event order.

## Feed contract

Packets are capped at 1,400 bytes to avoid IPv4 fragmentation under a 1,500-byte Ethernet MTU.

| Offset | Bytes | Field |
|---:|---:|---|
| 0 | 2 | magic and protocol version |
| 2 | 2 | reserved flags; must be zero |
| 4 | 2 | message count |
| 6 | 2 | exact datagram length |
| 8 | 8 | publisher session ID |
| 16 | 8 | first message sequence |
| 24 | 8 | monotonic source timestamp |
| 32 | 4 | CRC-32C over header and payload |

CRC-32C uses SSE4.2 or Armv8 intrinsics when available and a portable fallback otherwise. Encoding,
validation, and in-order decode allocate zero bytes in steady state.

`Multicast.RedundantGroup` and `RedundantPort` publish the same sealed packet on an optional B line.
Receivers merge both lines by session and sequence; the first valid copy wins.

Decoder invariants:

- A late joiner ignores incrementals until a snapshot establishes state.
- A gap invalidates every observed instrument; a snapshot repairs only its instrument.
- Incrementals never cross an unknown interval.
- Publisher restart changes the session, resets sequencing, and requires fresh snapshots.
- Delayed packets from retired sessions cannot take over the decoder.
- Conflicting A/B packets at the same session and sequence are counted as line divergence.
- Exact packet length, message boundaries, sides, quantities, snapshot order, and checksum are
  validated before state or sequence advances.
- Out-of-order packets are bounded. Missing ranges use session-scoped TCP gap fill; unavailable or
  oversized ranges require a snapshot.

The reconstructed book is either current or explicitly stale. It is never silently accepted after
modeled loss, corruption, late join, or restart.

## Durability contract

The publisher appends each sealed packet before either multicast send. One writer owns a journal;
message sequences are contiguous and session-bound. Startup truncates only an incomplete final
record. Bad CRCs, wrong sessions, sequence holes, and missing middle segments fail closed.

| Policy | Append acknowledgement |
|---|---|
| `OsBuffered` | bytes reached the OS page cache; process crash safe, power loss unsafe |
| `SyncPeriodic` | OS page cache; a timer runs `fsync` every `SyncInterval` while dirty |
| `SyncEachRecord` | each returned append passed `FileStream.Flush(true)` |

Checkpoints carry format version, session, sequence, exact length, commit trailer, and CRC-32C.
Restore validates into temporary state before replacing live books. Gap fill has request/response
CRCs, exact range coverage, session fencing, timeouts, range limits, bounded concurrency, and a
sparse index that refreshes as the WAL grows.
`Flush(true)` is the portable OS boundary; controller caches still require power-loss protection.

## Core data structures

The order-level matching book combines three structures:

| Structure | Purpose | Cost |
|---|---|---|
| `Dictionary<ulong, Order>` | Locate cancels and reductions | expected O(1) |
| Intrusive doubly linked FIFO | Time priority and unlink | O(1) |
| Price-indexed bitset | Touch and next occupied price | O(words crossed) |

Four aggregated depth books share one contract and are differentially checked after every generated
operation:

- `SortedArrayBook`: contiguous storage for shallow display depth.
- `VectorizedBook`: structure-of-arrays with AVX-512, AVX2, and scalar paths.
- `LadderBook`: bounded direct price index and occupancy bitset.
- `TreeBook`: sparse, unbounded prices; a deliberately different reference structure.

The SPSC ring uses padded monotonic cursors, release/acquire publication, power-of-two indexing, and
contiguous batch views. Capacity and release bounds fail fast instead of corrupting cursor state.

## Evidence

Release measurements on the checked-in host are observations, not portable constants. Tables below
are generated from the committed JSON; methodology and raw-artifact boundaries are in
[BENCHMARKS.md](BENCHMARKS.md).

<!-- generated: v2-headline -->
| Path | Recorded result | Contract |
|---|---|---|
| Feed encode → apply | 121.8 ns median | 0 B/op |
| Batched SPSC hand-off | 6.3 ns/item median | 0.066 B/item including harness setup |
| Matching at 100,000 resting orders | 98.7 ns/cycle median | state preserving |
| Committed NASDAQ samples | 39,998 transitions per implementation | exact |
| Seal + journal feed packet | 980.1 ns median | 0 B/op; OS-buffered acknowledgement |
| Loopback multicast, 500 subscribers | 485,393 delivered msg/s | 0 gaps; 0 CRC failures |
<!-- /generated -->

The committed AMZN and MSFT samples reproduce every independently verifiable next-book transition
across all four implementations. Full-session artifacts cover 978,218 exact transitions per
implementation across AMZN, GOOG, and MSFT.

The analytics suite separates explanation from prediction: order-flow imbalance has a strong
contemporaneous relationship with returns in the recorded AMZN session but less than 1% predictive
R². Stylized-fact checks also reject the simulator as a realistic price model. It is a deterministic
systems load generator; real market data remains the oracle for distribution-dependent research.

Earlier audience-scaling measurements are retained under a separate host and protocol boundary in
[BENCHMARKS.md](BENCHMARKS.md#transport-scaling-pre-v2-generation).

## Build and run

```bash
dotnet restore MarketDataSimulator.sln
dotnet build MarketDataSimulator.sln -c Release --no-restore
dotnet test Tests/Tests.csproj -c Release --no-build --no-restore
./scripts/smoke.sh
```

Run the simulator and reference client in separate terminals:

```bash
dotnet Server/bin/Release/net8.0/Server.dll Server/appsettings.json
dotnet Client/bin/Release/net8.0/Client.dll
```

Run the evidence suites:

```bash
dotnet run --project Bench -c Release -- protocol --iterations 1000000 --trials 7
dotnet run --project Bench -c Release -- durability --records 5000 --trials 5
dotnet run --project Bench -c Release -- queue --items 1000000 --trials 7
dotnet run --project Bench -c Release -- matching --sizes 100,1000,10000,100000
dotnet run --project Bench -c Release -- replay --data data/sample --trials 5
python3 bench/run_multicast.py --subscribers 100 500 --rates 500 --tag protocolv2
```

`ServerConfiguration` rejects invalid ports, duplicate instruments, unsafe depth and price bands,
non-finite rates, multicast addresses, aliased A/B endpoints, and invalid recovery intervals. A
fixed `Seed` makes instrument flow reproducible. `Multicast.Journal` enables the WAL and optional
retransmission port; every server start writes a distinct session directory.

## Real data

The committed `.csv.gz` files are small LOBSTER-derived slices of NASDAQ events and published depth.
CI verifies `data/sample/SHA256SUMS` before replay. `scripts/fetch-lobster.sh` retrieves larger AMZN,
GOOG, and MSFT samples from commit-pinned mirrors.

LOBSTER depth is finite: orders already resting below the published window are unknowable. The
correctness test therefore seeds from one published row, applies one event, and compares every
determined level with the next row. Deleting a level can shorten the comparable prefix. Cumulative
replay is reported separately as an observability study and is not mislabeled as parser error.

Native ITCH work should use the official
[Nasdaq ITCH directory](https://emi.nasdaq.com/ITCH/Nasdaq%20ITCH/) and handle the licensed schema
and redistribution terms explicitly.

## Repository map

```text
Common/Matching   sequencer-facing order book and depth projection
Common/Books      aggregated depth structures
Common/Feed       wire protocol, CRC, multicast, decoder state machine
Common/Durability journal, checkpoints, sparse range reads, retransmission
Common/Lobster    exact integer parser and replay oracle
Common/Analytics  streaming microstructure statistics
Server            deterministic simulator and validated configuration
Bench             protocol, matching, queue, replay, and transport harnesses
Tests             deterministic, adversarial, differential, and real-data tests
```

## Production boundary

A venue or trading platform would still require:

- a replicated and fenced sequencer, quorum commit, deterministic takeover, and cross-site archive;
- retention policy, sequencer-thread checkpoints, directory-metadata durability, and repair tooling;
- schema governance, compatibility tests, reference-data lifecycle, and session calendars;
- pre-trade risk, credit limits, kill switches, drop copy, audit retention, and authenticated
  entitlements for live and recovery channels;
- PTP-synchronized clocks with uncertainty, hardware timestamps, CPU/NUMA affinity, and NIC/kernel
  bypass where measurements justify them;
- redundant hosts and sites, deterministic failover, SLOs, telemetry, chaos drills, and disaster
  recovery.

Those are explicit next boundaries, not implications of a low local benchmark number.

## Design lineage

| Source | Applied constraint |
|---|---|
| [Signals and Threads: multicast](https://signalsandthreads.com/multicast-and-the-markets/) and [state-machine replication](https://signalsandthreads.com/state-machine-replication-and-why-you-should-care/) | global sequence, A/B lines, deterministic replay |
| [Jane Street: battle-tested systems](https://blog.janestreet.com/getting-from-tested-to-battle-tested/) | deterministic faults, simulated timing, state-machine invariants |
| [Cloudflare: million-packet UDP](https://blog.cloudflare.com/how-to-receive-a-million-packets/) | bounded socket work and a separate reliable repair channel |
| [Stripe: idempotency](https://stripe.com/blog/idempotency) and [rate limiters](https://stripe.com/blog/rate-limiters) | session identity, exact retries, cheap refusal, concurrency limits |
| [Netflix: performance under load](https://netflixtechblog.com/performance-under-load-3e6fa9a60581) | bound in-flight recovery before latency collapses |
| [Dan Luu: fsync failures](https://danluu.com/fsyncgate/) and [Mechanical Sympathy: false sharing](https://mechanical-sympathy.blogspot.com/2011/07/) | failure-specific durability claims and padded hand-off cursors |
| [HRT: devirtualisation](https://www.hudsonrivertrading.com/hrtbeat/optimising-compiler-performance-a-case-for-devirtualisation/) and [thenumb.at: open addressing](https://thenumb.at/Hashtables/) | compiler-visible hot paths and cache-coherent indexing |
| [Aeron Archive](https://aeron.io/docs/aeron-archive/overview/) and [Two Sigma metrics](https://www.twosigma.com/articles/building-a-high-throughput-metrics-system-using-open-source-software/) | position-based replay and measurement-led requirements |
