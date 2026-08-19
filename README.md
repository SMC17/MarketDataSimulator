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

> **Mean subscriber latency is set by the number of subscribers, not by the amount of work.**

TCP fan-out performs one write per subscriber per update, so disseminating a
single update spans N writes and a subscriber's latency is essentially its
position in that span. Two runs at an identical message rate but different
audience sizes make it plain:

<!-- generated: equal-work -->
| Subscribers | Feed rate | Fan-out | Mean latency |
|---|---|---|---|
| 100 | 100 upd/s | 9,490 msg/s | **1.61 ms** |
| 1,000 | 10 upd/s | 10,100 msg/s | **18.63 ms** |
<!-- /generated -->

Identical work per second; ten times the audience costs an order of magnitude
more latency, on a host with cores to spare in both runs. No amount of tuning
removes an O(N) term.

Exchanges solved this a long time ago, and not by tuning. They multicast: the
publisher sends once and the network performs the replication. Implementing that
here removed the term entirely — the server transmits at the *update* rate and
holds no subscriber table at all, so its packet rate is the same for 100
listeners as for 6,000:

<!-- generated: head-to-head -->
| Subscribers | Unicast mean | Multicast mean | Improvement |
|---|---|---|---|
| 100 | 1.61 ms | **0.31 ms** | **5.3×** |
| 500 | 8.54 ms | **0.85 ms** | **10.1×** |

Multicast was also measured at 250, 1,000, 2,000, 4,000, 6,000, 8,000 subscribers, where unicast was not run; those points are in the multicast sweep in BENCHMARKS.md.
<!-- /generated -->

Zero gaps, zero missed messages and zero stale subscribers at every sustained
point. The cost per delivered message is where the architectural difference
shows up most plainly:

<!-- generated: cost-per-message -->
| Transport | Highest sustained subscribers | Messages/s | Server CPU | Server CPU per message |
|---|---|---|---|---|
| Unicast gRPC | 900 | 86,621 | 229.4% | **26.48 µs** |
| Multicast | 6,000 | 594,067 | 73.2% | **1.23 µs** |

Multicast delivers each message for **21× less server CPU**, to **6.7× the subscribers** at **6.9× the throughput**.
<!-- /generated -->

Full methodology, the complete frontier, repeatability data and threats to
validity are in **[BENCHMARKS.md](BENCHMARKS.md)** — where every table is
generated from the raw result files and checked in CI, so no figure in either
document can drift from the measurement it came from.

---

## What is in here

```
Common/Matching/  Order-by-order book, price-time matching, depth projection
Common/Books/     Four aggregated book implementations behind one interface
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
whole price band (≈1 MB at NASDAQ's $0.0001 granularity), harmless at startup and ruinous when
a replay clears the book once per message. Both books now clear in time proportional to what
they hold rather than to the space they could hold.

The parser sustains **13.3–13.6M messages/sec at ~530 MiB/s with zero allocation**, keeping
timestamps as integer nanoseconds because a `double` cannot hold 34200.123456789 and
arithmetic on it would silently reorder events.

```bash
./scripts/fetch-lobster.sh                                    # ~185 MiB, not committed
dotnet run --project Bench -c Release -- replay --data data/lobster
```

A 20,000-message slice is committed, so CI validates against real market data offline.

### What the book is for

Reconstructing a book only matters if something can be computed from it. **Order flow
imbalance** — net pressure at the touch — over the real AMZN session, in non-overlapping
buckets:

<!-- generated: microstructure-amzn -->
| Bucket | Contemporaneous R² | t | Predictive R² | t |
|---|---|---|---|---|
| 10 events | 6.15% | 42.0 | 0.06% | 4.0 |
| 25 events | 8.44% | 31.5 | 0.38% | 6.4 |
| 50 events | 10.79% | 25.5 | 0.87% | 6.9 |
| 100 events | 14.69% | 21.5 | 0.71% | 4.4 |
| 250 events | 25.56% | 19.2 | 0.44% | 2.2 |
| 500 events | 30.66% | 15.4 | 0.00% | 0.1 |
<!-- /generated -->

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

<!-- generated: matching -->
| Resting orders | Add | Cancel | Match | Mixed (60/35/5) | Bytes/op |
|---|---|---|---|---|---|
| 100 | 74.5 ns | 112.9 ns | 108.6 ns | 107.3 ns | 39.8 |
| 1,000 | 71.2 ns | 116.6 ns | 123.1 ns | 113.8 ns | 40.8 |
| 10,000 | 61.9 ns | 130.1 ns | 130.1 ns | 129.7 ns | 38.0 |
| 100,000 | 64.5 ns | 250.9 ns | 141.0 ns | 96.7 ns | 15.3 |
<!-- /generated -->

Add is flat across a thousandfold range, which is the O(1) claim surviving contact with a
measurement. Cancel is flat to 10,000 resting orders and then roughly doubles at 100,000 — the
algorithm didn't change, the working set outgrew the cache. That knee is the interesting part,
and it's discussed in [BENCHMARKS.md](BENCHMARKS.md).

**Steady state allocates exactly zero bytes** — asserted, not hoped for. `AllocationTests` warms the
pools then requires 0 bytes across 200,000 iterations of matching, cancelling, publishing depth and
encoding the wire format. Orders and price levels are pooled, because GC pauses in a matching path
land at the worst possible moment.

### Order books

Four implementations of the same depth-limited book, chosen so the trade-offs
could be measured rather than argued:

| | Update | Touch | Publish top-10 | Notes |
|---|---|---|---|---|
| `SortedArrayBook` | O(log d) search + O(d) shift | O(1) | O(1) contiguous copy | Fastest publish at every depth |
| `LadderBook` | O(1) | O(1) amortised, bit-scan | O(d) bit-scan per level | Needs a bounded price band |
| `TreeBook` | O(log d) | O(log d) | O(d) pointer chase | Unbounded, sparse price spaces |
| `VectorizedBook` | branch-free SIMD count | O(1) | O(d) re-interleave | Struct-of-arrays, AVX-512 |

Measured, 4 vCPU, minimum of 7 trials over 200k operations:

<!-- generated: books-summary -->
| Depth | Mixed ns/op (array / simd / ladder / tree) | Top-10 publish ns/op | Bytes per publish |
|---|---|---|---|
| 10 | 23.6 / 17.8 / 17.5 / 38.9 | 5.3 / 13.7 / 39.8 / 102.1 | 0 / 0 / 0 / 104 |
| 32 | 31.1 / 23.1 / 20.0 / 55.9 | 5.3 / 14.2 / 40.0 / 120.5 | 0 / 0 / 0 / 136 |
| 64 | 33.8 / 27.0 / 20.1 / 62.0 | 5.3 / 13.5 / 38.8 / 121.8 | 0 / 0 / 0 / 152 |
| 128 | 37.6 / 30.0 / 21.0 / 68.4 | 5.6 / 13.8 / 39.7 / 132.8 | 0 / 0 / 0 / 168 |
| 1,000 | 49.0 / 40.3 / 27.0 / 94.5 | 5.3 / 13.7 / 40.2 / 141.0 | 0 / 0 / 0 / 200 |
<!-- /generated -->

The ranking **inverts between the write path and the publish path**, and that is
the whole result. The ladder's O(1) update wins at every depth measured; the
array's publish is flat at 5.3 ns at every depth, 2.6× faster than the next best
and up to 27× faster than the tree, because the top ten levels *are* the first
ten elements of an array and copying them does not care what follows. The tree
also **allocates on every publish** — enumerating a `SortedSet` allocates its
traversal stack — which on a dissemination path is not a throughput detail but a
source of collection pauses at arbitrary moments.

So the default is a trade rather than a walkover: `SortedArrayBook` gives up
about 6 ns per update to save 34 ns per publish against the ladder, which is the
right side of the trade only because this feed publishes on most updates. A
workload that absorbed many updates per published snapshot should pick
differently, and the interface exists so it can.

An earlier version of this table said the array won on *both* axes at depth 10.
It does not, and the reason it appeared to is worth more than the corrected
number: whichever depth the benchmark measured **first** reported inflated costs
for every implementation except the first one measured within it — an artefact of
JIT devirtualization at a shared interface call site and of background tier-1
compilation landing after the first configuration had finished. Reversing the
depth order moved the distortion to the other end of the sweep, which is how it
was caught. The benchmark now runs the whole sweep twice and records only the
second pass. The full account is in [BENCHMARKS.md](BENCHMARKS.md); the
transferable lesson is that a micro-benchmark result which depends on measurement
order is not a result.

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

168 tests, all deterministic and seeded. The interesting ones are not unit tests:

- **Differential testing** — random operation streams are applied to all four
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

These found real bugs the unit tests did not, each recorded in the commit that
fixed it — a stale-cache bug in the ladder's depth cap, a malformed packet that
could permanently desynchronise a consumer, false gap reports under reordering, a
gap flush that left the consumer silently behind the feed, and the same
stale-cache bug reappearing in the matching engine's price index.

That last one is the instructive one: having fixed it once, I wrote it again in
similar code. The response was not a third patch but to delete the duplication —
both books now share one `PriceIndex`, so the subtle part exists in exactly one
place and cannot be got wrong independently twice.

**A later adversarial review of the lock-free path found six more that the suite
could not see**, and the pattern in them is worth more than the list. Two were in
the statistics rather than the system: every dropped update was counted twice,
and updates the server deliberately declined to send were counted as sent. A
metric bug is not a lesser bug when the metric is what you are publishing. One
was a high-water mark updated with read-then-`Exchange` instead of
compare-and-swap, so concurrent producers could erase each other's maximum —
under-reporting the backlog precisely when the fan-out was falling behind. One
was arithmetic: mapping bid prices to sort keys by negation looks total on `int`
but is not, because `-int.MinValue` overflows to itself, so the worst possible
bid sorted as the best and the book came back crossed.

The last is the one that generalises. Fixing the key transform in one place did
not fix it, because `CopyTo` carried a *second*, hoisted copy of the same
arithmetic — so the book returned every price off by one until both went through
a single function. The same lesson as the `PriceIndex`, arrived at from the other
direction: a duplicated invariant is a bug with a delay fuse.

The suite grew from 135 tests to 168 in response, and each of those six has a
regression test that fails without its fix.

---

## Running it

```bash
dotnet build MarketDataSimulator.sln -c Release
dotnet test Tests/Tests.csproj -c Release       # 168 tests
./scripts/smoke.sh                              # end-to-end, both transports
```

Run the simulator and a reference subscriber:

```bash
cd Server/bin/Release/net8.0 && dotnet Server.dll        # unicast gRPC on :14000
cd Client/bin/Release/net8.0 && dotnet Client.dll        # then type: Subscribe 1
```

Benchmarks:

```bash
# Order book micro-benchmark
dotnet run --project Bench -c Release -- books --depths 10,32,64,128,1000

# Matching engine micro-benchmark
dotnet run --project Bench -c Release -- matching --sizes 1000,10000,100000

# Unicast dissemination sweep
python3 bench/run.py --subscribers 100 200 400 600 --rates 50 --tag scale50

# Multicast dissemination sweep
python3 bench/run_multicast.py --subscribers 100 1000 4000 --rates 50 --tag mcast

python3 bench/report.py scale50

# Real-data replay and the microstructure study (needs ./scripts/fetch-lobster.sh first)
dotnet run --project Bench -c Release -- replay --data data/lobster
dotnet run --project Bench -c Release -- study  --data data/lobster
```

Every run starts a fresh server process, so no run can contaminate the next, and
raw per-run JSON lands in `bench/results/`.

### Configuration

`Server/appsettings.json`, or any path passed as the first argument. Notable
settings: `BookImplementation` (`SortedArray`, `Vectorized`, `Ladder`, `Tree`), per-instrument
`Depth` / `UpdatesPerSecond` / `SnapshotProbability`, and the `Multicast` block
(`Enabled`, `Group`, `MaxBatch`, `SnapshotIntervalSeconds`).

---

## Notes on the environment

All measurements were taken on a 4 vCPU Intel Xeon with the load generator
running **on the same host**, over loopback; the exact machine is recorded in
`bench/results/environment.json` when the measurements are taken and reproduced
in [BENCHMARKS.md](BENCHMARKS.md). That matters: near the top of the unicast
range the harness consumes about as much CPU as the server, so the box rather
than the server is the binding constraint. The multicast figures carry their own
caveat — on a single host the kernel still replicates each datagram to every
subscriber's socket, work that switches would do on a real network. The
server-side cost is flat regardless, which is the claim being made.

Nothing here should be read as a good absolute number. The comparisons worth
something are the ones held against each other on the same box in the same
session: unicast against multicast, one book against another, the ring against
the channel.

`BENCHMARKS.md` lists the rest of the threats to validity, the sustained/not-sustained
criteria each run is judged against, and the run-to-run variance at each
operating point.
