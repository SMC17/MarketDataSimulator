# Market Data Simulator

A simulated exchange market data feed and the dissemination path that carries it
to subscribers — built to be measured, and then rebuilt around what the
measurements showed.

The system generates order book updates for a set of instruments and broadcasts
them to many concurrent subscribers, over either per-subscriber gRPC streams or
sequenced UDP multicast. It ships with a load-generating harness, an order book
micro-benchmark, a property-based test suite, and a written record of every
number it claims.

---

## The short version

Benchmarking the original design produced one result that mattered:

> **Mean subscriber latency grows linearly with the number of subscribers, and the server never exceeds two of four cores.**

Both follow from the same fact. TCP fan-out performs one write per subscriber per
update, so disseminating a single update spans N writes and a subscriber's
latency is essentially its position in that span. Two runs at an identical
message rate but different audience sizes make it plain:

| Subscribers | Feed rate | Fan-out | Mean latency |
|---|---|---|---|
| 100 | 100 upd/s | 10,000 msg/s | 2.79 ms |
| 1,000 | 10 upd/s | 10,000 msg/s | 23.92 ms |

Identical work per second; ten times the audience costs roughly nine times the
latency, on a host that was three-quarters idle. No amount of tuning removes an
O(N) term.

Exchanges solved this a long time ago, and not by tuning. They multicast: the
publisher sends once and the network performs the replication. Implementing that
here removed the term entirely — the server now emits **101.9 packets/s whether
100 or 4,000 subscribers are listening**:

| Subscribers | Unicast mean | Multicast mean | Multicast throughput | Server CPU |
|---|---|---|---|---|
| 100 | 2.79 ms | **0.34 ms** | 10,195 msg/s | 12% |
| 500 | 11.37 ms | **0.69 ms** | 50,999 msg/s | 15% |
| 1,000 | not sustained | **1.29 ms** | 101,997 msg/s | 18% |
| 4,000 | not sustained | **4.35 ms** | 408,084 msg/s | 31% |

Zero gaps and zero stale subscribers throughout. Peak measured throughput was
**1,001,892 messages/second** to 1,000 subscribers at 1.61 ms mean latency.

Batching then produced a result worth pausing on: packing messages into
datagrams cut mean latency by 2.4×, p99 by 7× and host CPU by 7.4×. Batching
normally *trades* latency for throughput — but in a broadcast system per-packet
cost is paid once per subscriber, so halving the packet count removes that work a
thousand times over, and the saturation causing the latency disappears with it.

Full methodology, the complete frontier, repeatability data and threats to
validity are in **[BENCHMARKS.md](BENCHMARKS.md)**.

---

## What is in here

```
Common/Matching/  Order-by-order book, price-time matching, depth projection
Common/Books/     Three aggregated book implementations behind one interface
Common/Feed/      Binary wire format, multicast publisher, decoder
Common/Server/    Unicast (gRPC) and multicast dissemination services
Server/           The simulated matching engine and its configuration
Client/           A reference subscriber that prints the feed
Bench/            Load generator, latency histogram, order book micro-benchmark
Tests/            Property-based and differential test suite
bench/            Sweep runners and raw results
```

### Validated against real NASDAQ data

The strongest correctness claim here is not self-referential. A real AMZN session from
2012-06-21 — 269,748 order events from LOBSTER's reconstruction of the NASDAQ ITCH feed,
with the exchange's own resulting order book after every one of them:

> **All four book implementations reproduce NASDAQ's published book exactly on 269,747 of
> 269,747 transitions — 100.0000%.**

Getting there took two attempts, and the first is the more instructive. A cumulative replay
matched 0.04% of rows, which looked like a serious bug — until an independent reconstruction
in Python agreed with the C# to the exact row count at every depth. Two implementations do not
share a bug that precisely, so the experiment was wrong, not the code: **a LOBSTER level-10
message file only contains events touching the top ten levels**, so cumulative reconstruction
is impossible by construction. The right test is the one a feed handler faces — *given the
book as it stands, does the next message produce the book the exchange publishes next?*

Real data also found what synthetic data could not: `LadderBook.Clear` was a memset over the
whole price band (≈1 MB at NASDAQ's $0.0001 granularity), harmless at startup and ruinous per
message — now 5.0× faster.

The parser sustains **12.5M messages/sec at 496 MiB/s with zero allocation**, keeping
timestamps as integer nanoseconds because a `double` cannot hold 34200.123456789 and
arithmetic on it would silently reorder events.

```bash
./scripts/fetch-lobster.sh                                    # ~71 MiB, not committed
dotnet run --project Bench -c Release -- replay --data data/lobster
```

A 20,000-message slice is committed, so CI validates against real market data offline.

### What the book is for

Reconstructing a book only matters if something can be computed from it. **Order flow
imbalance** — net pressure at the touch — over the real AMZN session, in non-overlapping
buckets:

| Bucket | Contemporaneous R² | t | Predictive R² | t |
|---|---|---|---|---|
| 10 events | 6.15% | 42.0 | 0.06% | 4.0 |
| 50 events | 10.79% | 25.5 | **0.87%** | **6.9** |
| 500 events | **30.66%** | 15.4 | 0.00% | 0.1 |

Contemporaneously the relationship is strong, reproducing Cont–Kukanov–Stoikov (2014) on this
session. **Predictively it is weak** — under 1% of variance everywhere, statistically real at
short horizons and gone by 500 events.

That gap is the point. A signal that explains what just happened is not one that predicts what
happens next, and conflating them is the usual way this analysis gets oversold. Buckets are
non-overlapping because overlapping windows share observations and inflate significance for free.

Both the monitor and the regression are O(1) per update and allocate nothing — a signal computed
off the feed path arrives too late to act on.

### Matching engine

The feed is the output of a real matching engine, not a random walk over price levels. Orders
arrive, rest, cancel and trade under **price-time priority**, and the depth feed is *derived* from
the resulting order-by-order events — one source of truth, so a subscriber applying the depth feed
necessarily agrees with the engine's book.

The book is the canonical structure for this job, three parts each covering what the others are bad
at:

| Structure | Covers | Cost |
|---|---|---|
| Hash map: order id → order | Cancel, the operation real flow is mostly made of | O(1) |
| Intrusive doubly-linked FIFO per price | Time priority within a level | O(1) unlink |
| Bitset price index + hardware bit-scan | Finding the touch to match against | O(1) amortised |

Add, cancel and reduce are O(1); matching is linear only in the number of orders actually filled,
never in the size of the book. Limit and market orders, GTC/IOC/FOK, and reduce-in-place that keeps
queue position (growing does not — that would let a participant reserve priority cheaply).

Measured, 200k operations, minimum of 5 trials:

| Resting orders | Add | Cancel | Match | Mixed (60/35/5) |
|---|---|---|---|---|
| 1,000 | 57.3 ns | **55.8 ns** | 100.9 ns | 70.1 ns |
| 10,000 | 51.1 ns | **57.9 ns** | 103.7 ns | 75.4 ns |
| 1,000,000 | 48.7 ns | 158.7 ns | 127.6 ns | 414.7 ns |

Cancel is flat from 1k to 10k orders and then climbs — the algorithm didn't change, the working set
outgrew the cache. That knee is the interesting part, and it's discussed in
[BENCHMARKS.md](BENCHMARKS.md).

**Steady state allocates exactly zero bytes** — asserted, not hoped for. `AllocationTests` warms the
pools then requires 0 bytes across 200,000 iterations of matching, cancelling, publishing depth and
encoding the wire format. Orders and price levels are pooled, because GC pauses in a matching path
land at the worst possible moment.

### Order books

Three implementations of the same depth-limited book, chosen so the trade-offs
could be measured rather than argued:

| | Update | Touch | Publish top-10 | Notes |
|---|---|---|---|---|
| `SortedArrayBook` | O(log d) search + O(d) shift | O(1) | O(1) contiguous copy | Fastest at display depths |
| `LadderBook` | O(1) | O(1) amortised, bit-scan | O(d) bit-scan per level | Needs a bounded price band |
| `TreeBook` | O(log d) | O(log d) | O(d) pointer chase | Unbounded, sparse price spaces |
| `VectorizedBook` | branch-free SIMD count | O(1) | O(d) re-interleave | Struct-of-arrays, AVX-512 |

Measured, 4 vCPU, minimum of 7 trials over 200k operations:

| Depth | Mixed ns/op (array / ladder / tree) | Top-10 publish ns/op | Bytes per publish |
|---|---|---|---|
| 10 | 24.9 / 44.2 / 46.4 | 11.3 / 257.3 / 160.5 | 0 / 0 / **104** |
| 100 | 38.1 / **20.5** / 77.5 | **8.1** / 37.7 / 209.7 | 0 / 0 / **152** |
| 1000 | 53.8 / **29.3** / 119.5 | **8.1** / 37.9 / 978.9 | 0 / 0 / **200** |

Two results worth stating. The ranking **inverts** between the write path and
the publish path — the ladder wins on updates from around depth 100, while the
array is 3–100× faster at producing the top ten levels, because that is a
contiguous copy rather than a walk. And the tree **allocates on every publish**
(enumerating a `SortedSet` allocates its traversal stack), which on a
dissemination path is not a throughput detail but a source of collection pauses.

For a depth-10 feed that publishes constantly, the array wins on both axes, and
that is the configured default. Asymptotics chose the wrong structure here;
measurement chose the right one.

### The feed protocol

Hand-rolled, fixed-layout, explicitly little-endian. Every field sits at a known
offset, so encoding is a handful of stores into a caller-supplied span and
decoding a handful of loads — no reflection, no schema walk, no allocation on
either path. Packets are capped below the Ethernet MTU, because a fragmented
datagram is lost in its entirety if any one fragment is dropped.

Multicast buys O(1) publishing at the price of reliability: there is no
retransmission and no backpressure, so a subscriber that falls behind loses
packets and the publisher never finds out. The feed is therefore **sequenced**,
which turns silent loss into detectable loss, and the consumer:

- **holds out-of-order packets** in a bounded buffer, because loss and reordering
  are indistinguishable at the moment a packet arrives;
- **declares a gap** when the buffer fills or a gap timer fires, and then marks
  itself **stale**;
- **refuses to apply incrementals while stale**, because a book built across a
  gap is wrong and gives no sign of it — strictly worse than admitting ignorance;
- **recovers** from the next periodic full snapshot;
- **arbitrates A/B lines** by discarding whichever copy of a packet arrives
  second, which is the same logic that suppresses network duplicates.

### Testing

117 tests, all deterministic and seeded. The interesting ones are not unit tests:

- **Differential testing** — random operation streams are applied to all three
  book implementations and their state compared after *every* operation, so a
  divergence is attributed to the operation that caused it.
- **Property-based testing** with **automatic shrinking** — failures are reduced
  to a minimal counterexample and reported with the seed that reproduces them.
- **Feed integrity** — that an incremental stream reconstructs the publisher's
  book exactly; that a snapshot resynchronises a drifted subscriber; that the
  book never crosses.
- **Adversarial transport** — gaps, duplicates, reordering, truncation, bogus
  message counts and foreign traffic, fed directly to a decoder that owns no
  socket, so the paths that are near-impossible to provoke over a real network
  are exercised deterministically.
- **Matching against a naive reference** — a deliberately slow, obviously-correct
  book that spells price-time priority out literally, compared trade-for-trade
  and queue-position-for-queue-position after every operation.
- **Conservation** — every unit submitted is filled, resting, or cancelled.
  Nothing is created or destroyed.
- **Allocation budgets** — zero bytes, enforced.
- **Real market data** — NASDAQ's own published book, per message.

These found five real bugs the unit tests did not, each recorded in the commit
that fixed it — a stale-cache bug in the ladder's depth cap, a malformed packet
that could permanently desynchronise a consumer, false gap reports under
reordering, a gap flush that left the consumer silently behind the feed, and the
same stale-cache bug reappearing in the matching engine's price index.

That last one is the instructive one: having fixed it once, I wrote it again in
similar code. The response was not a third patch but to delete the duplication —
both books now share one `PriceIndex`, so the subtle part exists in exactly one
place and cannot be got wrong independently twice.

---

## Running it

```bash
dotnet build MarketDataSimulator.sln -c Release
dotnet test Tests/Tests.csproj -c Release       # 117 tests
./scripts/smoke.sh                              # end-to-end, both transports
```

Run the simulator and a reference subscriber:

```bash
cd Server/bin/Release/net6.0 && dotnet Server.dll        # unicast gRPC on :14000
cd Client/bin/Release/net6.0 && dotnet Client.dll        # then type: Subscribe 1
```

Benchmarks:

```bash
# Order book micro-benchmark
dotnet run --project Bench -c Release -- books --depths 10,100,1000

# Matching engine micro-benchmark
dotnet run --project Bench -c Release -- matching --sizes 1000,10000,1000000

# Unicast dissemination sweep
python3 bench/run.py --subscribers 100 200 400 600 --rates 50 --tag unicast

# Multicast dissemination sweep
python3 bench/run_multicast.py --subscribers 100 1000 4000 --rates 50 --tag mcast

python3 bench/report.py unicast

# Real-data replay and the microstructure study (needs ./scripts/fetch-lobster.sh first)
dotnet run --project Bench -c Release -- replay --data data/lobster
dotnet run --project Bench -c Release -- study  --data data/lobster
```

Every run starts a fresh server process, so no run can contaminate the next, and
raw per-run JSON lands in `bench/results/`.

### Configuration

`Server/appsettings.json`, or any path passed as the first argument. Notable
settings: `BookImplementation` (`SortedArray`, `Ladder`, `Tree`), per-instrument
`Depth` / `UpdatesPerSecond` / `SnapshotProbability`, and the `Multicast` block
(`Enabled`, `Group`, `MaxBatch`, `SnapshotIntervalSeconds`).

---

## Notes on the environment

All measurements were taken on a 4 vCPU Intel Xeon at 2.80GHz with the load
generator running **on the same host**, over loopback. That is stated wherever a
number is, because it matters: near the top of the unicast range the harness
consumes about as much CPU as the server, so the box rather than the server is
the binding constraint. The multicast figures carry their own caveat — on a
single host the kernel still replicates each datagram to every subscriber's
socket, work that switches would do on a real network. The server-side cost is
flat regardless, which is the claim being made.

`BENCHMARKS.md` lists the rest of the threats to validity, the sustained/not-sustained
criteria each run is judged against, and the run-to-run variance at each
operating point.
