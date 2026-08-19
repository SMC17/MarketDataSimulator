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
| Recovery | Bounded reorder, per-instrument stale generations, atomic snapshots |
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
- Out-of-order packets are held within a fixed bound. `GapDetected` is the extension point for a
  retransmission service; periodic snapshots are the implemented recovery path.

The reconstructed book is either current or explicitly stale. It is never silently accepted after
modeled loss, corruption, late join, or restart.

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
| Loopback multicast, 500 subscribers | 485,393 delivered msg/s | 0 gaps; 0 CRC failures |
<!-- /generated -->

The committed AMZN and MSFT samples reproduce every independently verifiable next-book transition
across all four implementations. Full-session artifacts cover 978,218 exact transitions per
implementation across AMZN, GOOG, and MSFT.

The analytics suite separates explanation from prediction: order-flow imbalance has a strong
contemporaneous relationship with returns in the recorded AMZN session but less than 1% predictive
R². Stylized-fact checks also reject the simulator as a realistic price model. It is a deterministic
systems load generator; real market data remains the oracle for distribution-dependent research.

### Scaling with the audience (pre-v2 generation)

The table above measures the v2 protocol. How the architecture scales with the *number of
subscribers* is a separate question, measured before v2 on a different host — so these figures are
not comparable with the ones above and are never combined with them.

<!-- generated: cost-per-message -->
| Transport | Highest sustained subscribers | Messages/s | Server CPU | Server CPU per message |
|---|---|---|---|---|
| Unicast gRPC | 900 | 86,621 | 229.4% | **26.48 µs** |
| Multicast | 6,000 | 594,067 | 73.2% | **1.23 µs** |

Multicast delivers each message for **21× less server CPU**, to **6.7× the subscribers** at **6.9× the throughput**.
<!-- /generated -->

TCP fan-out costs one write per subscriber per update, so unicast latency tracks audience size
rather than workload: at a fixed 10,000 msg/s, 100 subscribers see 1.61 ms and 1,000 see 18.63 ms.
Multicast removes the term outright — the publisher's packet rate stays between 98.6 and 100.3/s
from 100 subscribers to 6,000, because it sends once and holds no subscriber table at all. Multicast
sustained 6,000 subscribers at 34.4 ms mean with zero gaps and zero stale receivers; 8,000 failed
with 594 detected sequence gaps, which is the sequencing machinery doing its job rather than
silently corrupting a book. Full sweeps, repeatability and threats to validity are in
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
dotnet run --project Bench -c Release -- queue --items 1000000 --trials 7
dotnet run --project Bench -c Release -- matching --sizes 100,1000,10000,100000
dotnet run --project Bench -c Release -- replay --data data/sample --trials 5
python3 bench/run_multicast.py --subscribers 100 500 --rates 500 --tag protocolv2
```

`ServerConfiguration` rejects invalid ports, duplicate instruments, unsafe depth and price bands,
non-finite rates, multicast addresses, aliased A/B endpoints, and invalid recovery intervals. A
fixed `Seed` makes instrument flow reproducible.

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
Common/Lobster    exact integer parser and replay oracle
Common/Analytics  streaming microstructure statistics
Server            deterministic simulator and validated configuration
Bench             protocol, matching, queue, replay, and transport harnesses
Tests             deterministic, adversarial, differential, and real-data tests
```

## Production boundary

A venue or trading platform would still require:

- a durable sequencer, write-ahead journal, checkpoints, catch-up, and retransmission;
- schema governance, compatibility tests, reference-data lifecycle, and session calendars;
- pre-trade risk, credit limits, kill switches, drop copy, audit retention, and entitlements;
- PTP-synchronized clocks with uncertainty, hardware timestamps, CPU/NUMA affinity, and NIC/kernel
  bypass where measurements justify them;
- redundant hosts and sites, deterministic failover, SLOs, telemetry, chaos drills, and disaster
  recovery.

Those are explicit next boundaries, not implications of a low local benchmark number.

## Design lineage

The design applies ideas discussed in Jane Street's *Signals and Threads*: sequenced broadcast and
redundant lines from [Multicast and the Markets](https://signalsandthreads.com/multicast-and-the-markets/),
deterministic replay from [State Machine Replication](https://signalsandthreads.com/state-machine-replication-and-why-you-should-care/),
hostile deterministic tests from [Why Testing Is Hard](https://signalsandthreads.com/why-testing-is-hard-and-how-to-fix-it/),
measurement discipline from [Performance Engineering on Hard Mode](https://signalsandthreads.com/performance-engineering-on-hard-mode/),
and monotonic-time semantics from [Clock Synchronization](https://signalsandthreads.com/clock-synchronization/).
