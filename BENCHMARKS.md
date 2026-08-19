# Dissemination benchmark

Measured capacity of the exchange-to-client broadcast path in this repository,
over two transports: per-subscriber gRPC streams, and sequenced UDP multicast.

Every number below was produced by the harness in this repo, on the machine
described under [Environment](#environment), and the raw per-run JSON is in
`bench/results/`.

## Contents

- [Headline](#headline)
- [What is being measured](#what-is-being-measured)
- [Environment](#environment)
- [Unicast: the O(N) wall](#unicast-the-on-wall)
- [Multicast: removing the term](#multicast-removing-the-term)
- [Head to head](#head-to-head)
- [Order book micro-benchmark](#order-book-micro-benchmark)
- [Repeatability](#repeatability)
- [Threats to validity](#threats-to-validity)
- [Reproducing](#reproducing)

---

## Headline

On a 4 vCPU host with the load generator running **on the same box**, with zero
dropped updates, zero detected gaps, and every subscriber receiving the complete
feed:

| Transport | Max sustained subscribers | Fan-out at that point | Mean latency | Server CPU |
|---|---|---|---|---|
| Unicast gRPC | 700 | 69,864 msg/s | 31.4 ms | 232% |
| **Multicast** | **8,000** | **814,645 msg/s** | **11.1 ms** | **44%** |

At matched subscriber counts the difference is starker still:

| Subscribers | Unicast mean | Multicast mean | Improvement |
|---|---|---|---|
| 100 | 2.79 ms | 0.34 ms | **8.2×** |
| 500 | 11.37 ms | 0.69 ms | **16.5×** |

Peak measured throughput was **1,001,892 messages/second** delivered to 1,000
subscribers at 1.61 ms mean latency, with the server at 40% of one core.

The single most important measurement in this document is not a latency figure.
It is that the server transmitted **101.9 packets per second in every multicast
run**, from 100 subscribers to 8,000 — the update rate, entirely independent of
the audience.

---

## What is being measured

**Subscriber** — one independent consumer of the feed. Under unicast, a
`StreamOrderbookUpdates` duplex gRPC stream on its own TCP connection. Under
multicast, a socket joined to the group with its own decoder and its own book.

**Update latency** — the interval between the server transmitting an update and
a subscriber's process receiving it. The publisher stamps
`Stopwatch.GetTimestamp()` onto the update as it enters the dissemination path;
the subscriber subtracts that from its own `Stopwatch.GetTimestamp()` on arrival.

Both processes run on the same host, and on Linux .NET's `Stopwatch` reads
`CLOCK_MONOTONIC`, which is machine-wide. The two readings are therefore directly
comparable — no clock synchronisation, and no halving of a round trip. This is a
genuine one-way, end-to-end measurement covering queueing, fan-out,
serialisation, the kernel path and the subscriber's receive path.

Reported latency is the mean over **every update delivered to every subscriber**
in the measurement window — not per update, and not per subscriber. An update
fanned out to 700 subscribers contributes 700 samples, so subscribers served late
in a fan-out are fully represented. This is deliberate: a per-update mean would
hide exactly the effect the unicast section is about.

**No coordinated omission.** The timestamp is applied when the update is
*produced*, not when a send is attempted, so a stalled dissemination path
inflates the latency of every update queued behind it rather than quietly
excluding them. Load is generated open-loop at a fixed rate: the generator does
not wait for subscribers, so a slow system produces a backlog rather than a
reduced offered rate.

**Delivered** — the share of the updates the server *actually published* that
reached subscribers, computed as `messages received per second ÷ (updates
published per second × subscribers)`, with the published rate read back from the
server's own telemetry rather than assumed.

This is deliberately not measured against the rate the harness was configured
for, and the distinction is not pedantic. The update generator does not always
hit its configured rate — on a slower host it produces 90 updates/s where 100
were asked for — and a ratio computed against the nominal rate books that
shortfall as though subscribers had lost data. Those are opposite failures: one
is the system under test failing, the other is the measuring instrument falling
short, and a single number that cannot tell them apart is not evidence about
either. They are reported as separate columns, `Delivered` and `Gen. rate`, and
only the first is part of the sustained criterion.

**Sustained** — a run only counts if all of the following hold:

| Criterion | Why it matters |
|---|---|
| every subscriber stayed connected for the whole run | a run that sheds subscribers is not serving them |
| delivered ≥ 99% of `subscribers × published rate` | every subscriber saw the whole feed |
| zero dropped updates | no subscriber overflowed its outbound queue |
| zero sequence gaps (multicast) | no subscriber silently lost data |
| zero stale subscribers (multicast) | no subscriber gave up on its book |
| shared update queue bounded | the fan-out kept pace with the matching engine |
| per-subscriber queues bounded (unicast) | no subscriber accumulated a backlog |

Runs failing these are still reported, but marked. A saturated system's mean
latency is a function of how long the run lasted, not of how fast it is.

---

## Environment

| | |
|---|---|
| CPU | Intel Xeon @ 2.80GHz, 4 vCPU, AVX-512 (AVX2/AVX512F/BW/DQ/VL, BMI2) |
| Memory | 15 GB |
| OS | Ubuntu 24.04.4 LTS, kernel 6.18.5 |
| Runtime | .NET 8.0.30, all projects target `net8.0` |
| Build | Release, Server GC |
| Transports | gRPC over HTTP/2 cleartext; UDP multicast on 239.7.7.7 — both over loopback |
| Topology | server and load generator as separate processes on the same host |
| Instruments | 2, depth 10, 5% of updates are full snapshots |
| Per run | 8 s warm-up discarded, 20–25 s measured, fresh server process per case |

---

## Unicast: the O(N) wall

Feed rate held constant at 100 updates/s aggregate; subscriber population varied.

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

At 800 the host is at 390% of 400%, per-subscriber queues pass 100 messages, and
delivery above 100% is a backlog draining rather than extra data.

A second sweep holds the *message rate* constant instead, on a lighter feed:

| Subscribers | Feed | Fan-out (msg/s) | Mean (ms) | p99 | Server CPU | Host CPU | Sustained |
|---|---|---|---|---|---|---|---|
| 1,000 | 10 upd/s | 10,000 | **23.92** | 47.4 | 53% | 94% | yes |
| 2,000 | 10 upd/s | 19,995 | **42.42** | 90.0 | 96% | 176% | yes |
| 3,000 | 10 upd/s | 29,989 | **57.88** | 136.4 | 123% | 235% | yes |
| 4,000 | 10 upd/s | 39,998 | **95.64** | 278.6 | 162% | 331% | yes |
| 5,000 | 10 upd/s | 52,445 | 469.15 | 1695.0 | 191% | 376% | **NO** |

### What the two sweeps say together

Put one row from each beside the other:

| Run | Subscribers | Feed | Fan-out | Mean latency | Host CPU |
|---|---|---|---|---|---|
| A | 100 | 100 upd/s | 10,000 msg/s | 2.79 ms | 66% of 400% |
| B | 1,000 | 10 upd/s | 10,000 msg/s | 23.92 ms | 94% of 400% |

Identical work per second. Ten times the audience costs 8.6× the latency, on a
box that was three-quarters idle — so this is not CPU starvation. It is the
fan-out span: every update is written once per subscriber, and a subscriber's
latency is essentially its position in that sequence of writes.

**Throughput** is bounded separately, by CPU cost per message. At the top of the
sustained range (700 subscribers, 69,864 msg/s, 232% CPU) the server spends
about **33 µs of CPU per delivered message**, which is what puts the ceiling near
70–80k msg/s once the co-resident harness takes its share. The dominant term is
the per-write trip through the `Grpc.Core` C-core interop layer.

Notice also that the server never exceeds ~232% of 400%. It cannot use the whole
machine, because dissemination is serialised behind a fan-out that must visit
every subscriber in turn.

### Before the fan-out rework

The original implementation awaited each subscriber's network write *inside* the
broadcast loop, rebuilt the wire message once per subscriber, and allocated a
`HashSet` per subscriber per update merely to test subscription:

| Subscribers | Original mean | Original fan-out | Reworked mean | Reworked fan-out |
|---|---|---|---|---|
| 200 | 82.4 ms | 19,988 msg/s | **3.52 ms** | 20,001 msg/s |
| 300 | 5,240 ms | 21,072 msg/s | **6.71 ms** | 30,002 msg/s |
| 400 | 7,953 ms | 20,520 msg/s | **9.74 ms** | 39,904 msg/s |

It ceilinged at roughly 20,000 msg/s and collapsed past ~200 subscribers — at 300
and 400 its fan-out rate is flat at the ceiling while latency grows without
bound, the signature of a queue that never drains. Its best sustained result was
150 subscribers at 8.0 ms.

The rework (encode once per update; lock-free subscription test against a
copy-on-write snapshot; a bounded queue per subscriber drained by its own pump,
so the broadcast thread never touches the network) bought roughly 3.5× the
throughput and 4× the subscribers. It did **not** remove the O(N) term, because
nothing at this layer can.

---

## Multicast: removing the term

Same matching engine, same feed rate, same measurement. The publisher encodes
each update once and sends a single datagram; the network performs the
replication.

| Subscribers | Fan-out (msg/s) | Mean (ms) | p50 | p99 | Max | Gaps | Stale | Server pkts/s | Server CPU | Host CPU |
|---|---|---|---|---|---|---|---|---|---|---|
| 100 | 10,195 | **0.34** | 0.29 | 0.71 | 16.4 | 0 | 0 | 101.9 | 12% | 8% |
| 250 | 25,487 | **0.46** | 0.44 | 1.15 | 4.7 | 0 | 0 | 101.9 | 13% | 12% |
| 500 | 50,999 | **0.69** | 0.68 | 1.54 | 7.1 | 0 | 0 | 101.9 | 15% | 9% |
| 1,000 | 101,997 | **1.29** | 1.24 | 3.38 | 10.0 | 0 | 0 | 101.9 | 18% | 41% |
| 2,000 | 203,998 | **3.25** | 2.89 | 9.88 | 29.3 | 0 | 0 | 101.9 | 22% | 166% |
| 4,000 | 408,084 | **4.35** | 4.31 | 9.66 | 50.4 | 0 | 0 | 101.9 | 31% | 216% |
| 6,000 | 611,976 | **7.27** | 7.15 | 16.8 | 47.3 | 0 | 0 | 102.0 | 39% | 300% |
| 8,000 | 814,645 | **11.14** | 9.96 | 35.1 | 93.5 | 0 | 0 | 101.8 | 44% | 377% |

Every run: zero gaps, zero missed messages, zero stale subscribers, zero
malformed packets.

**The `Server pkts/s` column is the result.** It does not move. The publisher
transmits at the update rate and has no idea how many subscribers exist —
`MulticastOrderbookService` contains no subscriber table at all.

Server CPU rises from 12% to 44% across an 80× increase in audience, and that
residual is not fan-out work: on a single host the kernel replicates each
datagram into every subscriber's socket buffer, and some of that cost is charged
to the sender's softirq context. On a real network that replication is done by
switches. Host CPU — which includes all 8,000 receivers — is what reaches 377%,
and that is what bounds this measurement at 8,000, not the server.

---

## Batching: why fan-out inverts the usual trade

Batching normally trades latency for throughput — you wait to accumulate work,
so each item leaves later. Measured on a fan-out feed, it does the opposite.

1,000 subscribers, 1,000 updates/s aggregate (≈1M messages/s delivered), varying
only how many messages the publisher packs into a datagram:

| Max batch | Messages/s | Server pkts/s | Mean (ms) | p99 (ms) | Server CPU | Host CPU |
|---|---|---|---|---|---|---|
| 1 | 973,783 | 933.8 | 3.63 | 24.35 | 56% | **383%** |
| 4 | 1,001,892 | 307.8 | 1.61 | 3.85 | 40% | 168% |
| 16 | 1,001,636 | 187.9 | **1.56** | 3.41 | 32% | 56% |
| 64 | 1,001,734 | 186.7 | **1.54** | **3.40** | **32%** | **52%** |

Batching cut mean latency by 2.4×, p99 by 7×, and host CPU by 7.4×.

The reason is that per-packet cost in a broadcast system is not paid once — it is
paid once per subscriber. Every datagram costs the sender a syscall and costs
each of 1,000 receivers a wakeup, a copy and a trip through the socket layer.
Halving the packet count removes that work 1,000 times over. At `MaxBatch = 1`
the host is at 383% of 400% and messages queue behind saturated receivers, which
is where the extra 2 ms of latency comes from; batching removes the saturation,
and the latency goes with it.

Batching is not free of the usual trade — it is still bounded by the flush
deadline, and a genuinely idle feed would see a message wait for it. But when the
audience is large, the amortisation is multiplied by the audience and dominates.

Note the packet rates at `MaxBatch` 16 and 64 are identical (≈187/s, about 5.4
messages per packet). Neither is hitting its batch limit: the binding constraint
is the 1 ms flush deadline, which under load resolves closer to 5 ms because
`Task.Delay` cannot do better. A publisher that needed tighter control would
spin or use a timer wheel rather than the thread pool timer.

---

## Head to head

| Subscribers | Unicast mean | Multicast mean | Unicast server CPU | Multicast server CPU |
|---|---|---|---|---|
| 100 | 2.79 ms | **0.34 ms** | 57% | **12%** |
| 250 | ~5 ms | **0.46 ms** | ~110% | **13%** |
| 500 | 11.37 ms | **0.69 ms** | 202% | **15%** |
| 700 | 31.42 ms | — | 232% | — |
| 800 | *not sustained* | — | — | — |
| 1,000 | *not sustained* | **1.29 ms** | — | **18%** |
| 8,000 | *not sustained* | **11.14 ms** | — | **44%** |

Cost per delivered message, at each transport's sustained ceiling:

| | Subscribers | Messages/s | Server CPU | CPU per message |
|---|---|---|---|---|
| Unicast | 700 | 69,864 | 232% | **33.2 µs** |
| Multicast | 8,000 | 814,645 | 44% | **0.54 µs** |

A 61× reduction in server CPU per delivered message, and 11× the subscribers at
11.7× the throughput.

The latency curve still rises with subscriber count under multicast (0.34 ms at
100, 11.1 ms at 8,000) — but for a different reason. The publisher's cost is
flat; what grows is the kernel's local replication and the contention among 8,000
co-resident receivers on four cores. That is an artefact of measuring on one box,
not a property of the design.

### What multicast costs

It is not free, and the trade is the whole reason the sequencing machinery
exists. UDP multicast has no retransmission and no backpressure: a subscriber
that falls behind loses packets and the publisher never learns of it. In exchange
for O(1) publishing you take on:

- **Detection instead of prevention** — sequence numbers on every packet turn
  silent loss into detectable loss.
- **Reordering tolerance** — loss and reordering are indistinguishable when a
  packet arrives, so out-of-order packets are held in a bounded buffer, and a gap
  is only declared when that buffer fills or a gap timer fires.
- **Staleness over guessing** — a consumer that has detected a gap refuses to
  apply incrementals until a snapshot restores a known book. A book built across
  a gap is wrong and gives no sign of it.
- **A recovery channel** — the periodic full snapshot bounds how long a gapped
  subscriber stays dark.
- **Optional A/B redundancy** — two lines over disjoint paths, arbitrated by
  discarding whichever copy arrives second. One extra send per packet, still
  independent of the audience, and it roughly squares the probability that a
  packet is lost to any given subscriber.

---

## Validation against real NASDAQ market data

Everything above measures speed. This measures whether the thing is *right*, against data
this project did not produce.

**Data.** A real AMZN session, 2012-06-21, from LOBSTER's reconstruction of the NASDAQ ITCH
feed: 269,748 order events, and the exchange's own resulting order book after every one of
them. 131,954 new limit orders, 123,458 deletions, 2,917 partial cancels, 8,974 visible
executions, 2,445 hidden executions.

**Result.** All four book implementations reproduce NASDAQ's published book **exactly on
269,747 of 269,747 transitions** - 100.0000%.

| Implementation | Transitions verified | Exact | Accuracy | Transitions/s |
|---|---|---|---|---|
| SortedArrayBook | 269,747 | 269,747 | **100.0000%** | 928,762 |
| VectorizedBook | 269,747 | 269,747 | **100.0000%** | 954,719 |
| LadderBook | 269,747 | 269,747 | **100.0000%** | 188,483 |
| TreeBook | 269,747 | 269,747 | **100.0000%** | 222,698 |

### Why it is a transition test, and not a replay

The first attempt replayed the whole session cumulatively from a seeded book and matched
0.04% of rows. That looks like a serious bug, so the first move was to rule the code out: an
independent reconstruction written in Python agreed with the C# to the exact row count at
every depth (5,585 / 4,002 / 3,066 / ... / 106 rows correct at depths 1 through 10). Two
independent implementations agreeing that precisely do not share a bug - the experiment was
wrong, not the code.

The cause is a property of the data. **A LOBSTER level-10 message file contains only events
that touch the top ten levels**; anything deeper is filtered out. Cumulative reconstruction is
therefore impossible by construction, because the events that would maintain the deeper book
are simply absent. The evidence is decisive: zero of 269,747 messages fell outside the
published ten-level window, which is only possible if the file was filtered to it.

So the test asks the question a feed handler actually has to answer: **given the book as it
stands, does applying the next message produce the book the exchange publishes next?** Seeding
from the reference before each message keeps transitions independent, making this a quarter of
a million separate assertions rather than one trajectory that stops meaning anything after an
early divergence.

One slot of slack is left at the bottom of the window: a message that removes a level promotes
an unknown eleventh level into view, so nine levels are compared rather than ten. Steps whose
outcome a ten-level snapshot cannot determine are counted as unverifiable rather than scored -
there were none.

### What real data exposed that synthetic data could not

**A performance bug.** `LadderBook.Clear` was a memset over the entire price band. At NASDAQ's
$0.0001 tick granularity a ten-dollar band is 108,300 slots, so clearing cost roughly a
megabyte of memset regardless of whether ten levels were resting. Invisible when a book is
cleared once at startup; ruinous when cleared per message. Walking the occupancy index instead
touches only live levels - **5.0x faster**. `SortedArrayBook` had the same shape of problem and
is now constant-time to clear - **2.4x faster**.

**A structural weakness of the ladder.** It remains the slowest here, and for a reason worth
knowing: its bit-scan walks the price *band*, and a real book at $0.0001 granularity occupies
about 0.02% of a ten-dollar band. Sparse real prices are exactly the case a synthetic benchmark
with a tight band never produces.

### Parser

The LOBSTER CSV parser sustains **12.5M messages/sec at 496 MiB/s and allocates zero bytes**.
Spans and hand-rolled integer parsing throughout - no strings, no substrings, no decoding.
Delimiter search uses the runtime's vectorised `IndexOf`.

Timestamps are parsed to integer nanoseconds rather than `double`. LOBSTER carries nine
fractional digits; a `double` holds about fifteen significant decimal digits in total, so
34200.123456789 is representable only barely and does not survive arithmetic. Integer
nanoseconds are exact, and exactness is what lets two events be ordered confidently.

---

## Does the book predict anything? Order flow imbalance on real data

Reconstructing a book is only worth doing if something can be computed from it. The canonical
something is **order flow imbalance** — net pressure at the touch, attributing every change in
the best quotes to buying or selling interest. Size added at the bid and size removed from the
ask both count as buying pressure; it is deliberately blind to whether a departure was a
cancellation or a trade, because the book cannot tell and the distinction does not matter to
the signal.

Run over the AMZN session, bucketed into non-overlapping blocks of events:

| Bucket (events) | Samples | Contemporaneous R² | slope | t | Predictive R² | slope | t |
|---|---|---|---|---|---|---|---|
| 10 | 26,974 | 6.15% | 0.192 | 42.0 | 0.06% | 0.019 | 4.0 |
| 25 | 10,789 | 8.44% | 0.216 | 31.5 | 0.38% | 0.045 | 6.4 |
| 50 | 5,394 | 10.79% | 0.248 | 25.5 | **0.87%** | 0.070 | **6.9** |
| 100 | 2,697 | 14.69% | 0.295 | 21.5 | 0.71% | 0.064 | 4.4 |
| 250 | 1,078 | 25.56% | 0.438 | 19.2 | 0.44% | 0.057 | 2.2 |
| 500 | 539 | **30.66%** | 0.458 | 15.4 | 0.00% | 0.004 | 0.1 |

Slope is half-ticks of mid change per unit of imbalance. Every contemporaneous slope is
positive: buying pressure raises the price, as it must.

**Contemporaneously the relationship is strong** — 6% of variance explained at ten events,
rising to 31% at five hundred, with t-statistics from 15 to 42. That reproduces the
Cont–Kukanov–Stoikov (2014) result on this session. R² rising with bucket size is expected:
aggregation averages out the noise in individual updates while the signal accumulates.

**Predictively it is much weaker, and that is the honest headline.** Using the *previous*
bucket's imbalance to predict the *next* bucket's move — strictly out of sample in time, which
is the only version that could be traded — explains under 1% of variance everywhere. It is
statistically real at short horizons (t ≈ 6.9 at 50 events, on 5,394 non-overlapping samples)
and gone entirely by 500 events (t = 0.1).

That gap between 31% and 0.87% is the whole point. A signal that explains what just happened is
not a signal that predicts what happens next, and reporting the contemporaneous number as though
it were predictive is the most common way this analysis is oversold. The buckets here are
non-overlapping precisely because overlapping windows share observations, and the resulting
autocorrelation inflates significance for free.

Both the monitor and the regression are incremental, O(1) per update, and allocate nothing —
asserted by test. A signal computed off the feed path is a signal that arrives too late to act
on. The regression uses Welford-style updating rather than raw sums of squares, because a
session-long flow accumulator sits far from zero and the textbook formula would lose most of its
significant digits differencing two large near-equal numbers.

```bash
dotnet run --project Bench -c Release -- study --data data/lobster
```

---

## Order book micro-benchmark

Three implementations of the same depth-limited book, each running the identical
pre-generated operation stream. 200,000 operations, 7 trials, minimum reported —
the fastest observed run is the one least perturbed by scheduling noise, which
for a CPU-bound micro-benchmark estimates true cost better than an average.

| Depth | Implementation | Mixed ns/op | Touch ns/op | Top-10 publish ns/op | Bytes per publish |
|---|---|---|---|---|---|
| 10 | SortedArrayBook | **24.9** | 3.7 | **11.3** | 0 |
| 10 | LadderBook | 44.2 | 17.6 | 257.3 | 0 |
| 10 | TreeBook | 46.4 | 45.5 | 160.5 | 104 |
| 100 | SortedArrayBook | 38.1 | 2.6 | **8.1** | 0 |
| 100 | LadderBook | **20.5** | 4.7 | 37.7 | 0 |
| 100 | TreeBook | 77.5 | 8.4 | 209.7 | 152 |
| 1000 | SortedArrayBook | 53.8 | 4.2 | **8.1** | 0 |
| 1000 | LadderBook | **29.3** | 4.8 | 37.9 | 0 |
| 1000 | TreeBook | 119.5 | 12.7 | 978.9 | 200 |

Three results:

1. **The array beats the tree at every depth measured on the update path**,
   despite worse asymptotics — O(log d) search plus an O(d) shift against
   O(log d). A depth-10 side is 80 bytes: one cache line, shifted by a `memmove`
   the hardware executes at many bytes per cycle. The tree pays a dependent cache
   miss per level of descent, and one miss costs more than the whole shift.
   Complexity classes describe how cost *scales*, not what it *is* at a given
   size.

2. **The ranking inverts between the write path and the publish path.** The
   ladder wins on mixed updates from around depth 100 (O(1), no comparisons, no
   data movement), but the array is 3–100× faster at producing the top ten
   levels, because that is a contiguous copy rather than a walk. Which structure
   is "best" depends entirely on the read/write mix, and a market data feed
   publishes constantly.

3. **The tree allocates on every publish** — 104–200 bytes, growing with depth,
   because enumerating a `SortedSet` allocates its traversal stack. On a
   dissemination path that is not a throughput detail; it is a source of garbage
   collection pauses at arbitrary moments. The array and ladder allocate nothing.

For a depth-10 feed that publishes constantly, the array wins on both axes, and
it is the configured default. Measurement chose it; asymptotics would not have.

### The SIMD book, and why it is not the default

`VectorizedBook` replaces the binary search with a branch-free vector count, on the theory that
what actually costs a binary search at these sizes is not the comparisons but the branch
mispredictions - each step depends on the last, and the predictor has nothing to work with on
random prices. Two changes make that possible: prices move into their own contiguous `int[]`
(struct of arrays), and bids are negated so both sides ascend and a price's position is simply
the count of keys below it. Locating a price becomes one vector load, one compare, one mask
extract, one population count, on 16 lanes at a time under AVX-512.

| Depth | SortedArray | Vectorized | Ladder | Tree |
|---|---|---|---|---|
| 10 | **24.9** | 41.7 | 32.3 | 44.5 |
| 32 | 32.7 | **21.9** | 17.9 | 62.6 |
| 64 | 38.0 | **24.2** | 20.3 | 70.6 |
| 1000 | 54.2 | **44.6** | 28.6 | 105.1 |

Mixed operations, ns/op. It wins from roughly 32 levels up - and loses at depth 10, which is
the depth this feed actually publishes.

Two things cost it. A branch-free scan is O(n) where a binary search is O(log n), so the vector
path is used only below a measured crossover of 64 levels and a binary search takes over above
it; before that hybrid, depth 1000 cost 96.1 ns instead of 44.6. And struct-of-arrays has to be
paid for twice - an insert shifts two arrays instead of one, and publishing has to re-interleave
prices and quantities rather than issuing a single memory copy. That second cost fell from
177 ns to 11 ns per publish once the sign was hoisted and the bounds checks eliminated, but it
does not go away.

So the fastest search does not win, because search is not what a depth-10 market data book
spends its time on. `SortedArrayBook` stays the default. The interesting result is not that
SIMD is fast; it is that it was measured, found to win only in a band this workload does not
occupy, and left switchable rather than adopted.

---

## Matching engine micro-benchmark

The book that produces the feed is order-by-order with price-time priority: an id map from order
id to the order object, an intrusive FIFO per price level, and a bitset price index for the touch.
That gives O(1) add, O(1) cancel, O(1) reduce, and matching linear only in the number of orders
actually filled.

The sweep across book sizes exists to make those claims falsifiable. An O(1) cancel should cost
the same with a thousand resting orders as with a million; if the measured cost climbs, the claim
is wrong whatever the code looks like. Price-level churn is held out of the cancel column - every
order sits on one of 64 fixed levels - so what is measured is one hash lookup and one unlink.

200,000 operations, minimum of 5 trials:

| Resting orders | Add ns/op | Cancel ns/op | Match ns/op | Mixed ns/op |
|---|---|---|---|---|
| 100 | 125.5 | 127.7 | 97.8 | 73.9 |
| 1,000 | 57.3 | **55.8** | 100.9 | 70.1 |
| 10,000 | 51.1 | **57.9** | 103.7 | 75.4 |
| 100,000 | 48.4 | 82.2 | 105.9 | 85.9 |
| 1,000,000 | 48.7 | 158.7 | 127.6 | 414.7 |

Mixed is 60% add, 35% cancel, 5% aggressive — roughly a real venue's message profile, where
cancels vastly outnumber trades.

**Cancel is flat at ~56 ns from 1,000 to 10,000 resting orders, then climbs to 159 ns at a
million.** The algorithm did not change; the working set did. At ten thousand orders the id map
and the order objects together are on the order of a megabyte and sit in cache. At a million they
are roughly a hundred megabytes, and every cancel becomes two dependent trips to main memory — the
map lookup, then the order object it points at. The knee lands exactly where the working set
leaves the last-level cache.

This is the same lesson as the aggregated book benchmark, in the other direction: complexity
classes describe how cost scales with *n*, and say nothing about what a constant-time operation
actually costs once *n* stops fitting in cache. An order book sized for a real venue lives on the
right-hand side of that table, which is an argument for compact, pooled, contiguous storage rather
than for a cleverer algorithm.

The 100-order row is slower than the 1,000-order row because at that size the 64 price levels are
sparsely populated, so cancels empty levels and adds recreate them; the level churn dominates.

### Allocation

The matching path allocates nothing in steady state, and that is asserted rather than measured:
`Tests/AllocationTests.cs` warms the pools and then requires **exactly zero bytes** across 200,000
iterations of add/cancel, of matching, of depth publishing, of aggregated-book publishing, and of
wire encoding. Orders and price levels are recycled, because a venue's steady state is a torrent of
arrivals and cancellations at roughly constant book size, and a fresh object per order would hand
the collector hundreds of megabytes an hour of pure churn — with the resulting pauses landing in
the matching path, the one place that cannot absorb them.

An allocation budget only holds if something enforces it. A single incautious edit reintroduces
allocation silently, and nothing else in a test suite would notice.

---

## A lock-free queue, and why it did not help

`RingBuffer<T>` is a bounded single-producer/single-consumer queue with no locks and no
allocation: each side owns its own cursor, published with a release store and read with an
acquire load, so neither side ever performs an interlocked read-modify-write. The cursors are
padded onto separate cache lines — without that, the producer's write index and the consumer's
read index share a line and every write invalidates the other core's copy, which is enough on
its own to make a lock-free queue slower than a locked one.

In isolation it is decisively faster than the `Channel` it was built to replace:

| Queue | ns/item | M items/s | B/item |
|---|---|---|---|
| RingBuffer, single thread | **3.0** | 338.6 | 0 |
| Channel, single thread | 50.9 | 19.7 | 0 |
| RingBuffer, producer + consumer | **5.8** | 173.1 | 0 |
| RingBuffer batched, producer + consumer | **4.9** | 205.1 | 0 |
| Channel, producer + consumer | 118.5 | 8.4 | 0 |

**20.5× the channel's throughput on the concurrent hand-off. It made no measurable difference
end to end.**

Wired into the dissemination path behind `UseRingQueue` and measured at 500 subscribers, three
trials each, the two are indistinguishable — channel means of 8.7, 14.9 and 23.1 ms against ring
means of 20.3 and 21.6 ms. The spread within each option is larger than the gap between them, so
the honest reading is *no difference*, not *the ring is worse*.

The arithmetic says why, and it is worth doing before optimising rather than after:

> The hand-off sits **upstream of fan-out**. It carries the *update* rate — 100/s — not the
> *message* rate of 50,000/s, because one update becomes 500 messages only after it leaves the
> queue. Saving 48 ns on 100 operations per second is **4.8 µs/s, or 0.0005% of one core.**

No queue implementation could have mattered there. The 33 µs/message the server actually spends
is in the gRPC write path, which is downstream and runs 500× more often.

The ring stays in the repository, switchable and defaulted off, because the measurement is the
point: it is a correct, tested, genuinely 20×-faster component in the wrong place. The lesson is
not that lock-free queues are useless — it is that an optimisation's value is set by how often
its code runs, and on a fan-out path the amplification happens *after* this stage.

Two further notes for anyone tempted to put it somewhere else here. It spin-waits before
blocking, which is right for one dedicated consumer thread and badly wrong for per-subscriber
queues — thousands of spinning subscribers would burn every core doing nothing. And it is SPSC
by construction, so each matching engine gets its own ring and the consumer drains them
round-robin, rather than several producers sharing one queue and needing an interlocked cursor
after all.

---

## Repeatability

Each configuration was run four times, with a fresh server process each time.

| Point | Runs | Median | Min | Max | Spread |
|---|---|---|---|---|---|
| Unicast, 500 subscribers, 100 upd/s | 4 | 11.47 ms | 10.53 | 12.38 | 1.17× |
| Unicast, 4,000 subscribers, 10 upd/s | 4 | 54.59 ms | 47.13 | 95.64 | 2.03× |

Individual means — 500: 11.37, 10.53, 12.38, 11.57 ms. 4,000: 95.64, 47.13,
55.23, 53.95 ms.

The 4,000-subscriber spread is the important caveat. The 95.64 ms in the sweep
table is the slowest of four runs at that configuration; the median is nearer
55 ms. At that population the result is sensitive to how 4,000 connections happen
to land across threads and garbage collection, and a single sweep point does not
characterise it. The 500-subscriber point is stable enough to quote directly.

Read every figure at high subscriber counts as ±a factor of two unless it has
repeat runs behind it.

---

## Threats to validity

Read these numbers as "this host, this topology", not as properties of the
design.

- **The load generator shares the host.** Near the top of the unicast range the
  harness consumes roughly as much CPU as the server, so the box — not the
  server — is the binding constraint. The `Host CPU` column shows the remaining
  headroom.
- **Multicast replication is local.** On one machine the kernel copies each
  datagram into every subscriber's socket buffer, work that switches would do on
  a real network. The publisher's cost is genuinely flat, and that is the claim;
  the total host cost is not, and the multicast latency curve reflects it.
- **Loopback, not a network.** No NIC, no switch, no propagation delay, and —
  importantly for multicast — **no real packet loss**. Zero gaps across every run
  is a property of loopback, not evidence that the recovery machinery works. That
  evidence comes from the test suite, which injects loss, duplication,
  reordering and corruption directly.
- **Variance grows with population**, as the repeatability section shows.
- **One instrument set** — two instruments at depth 10. Depth changes snapshot
  size and therefore bytes per update.
- **Snapshots are 5% of updates.** Snapshots are far larger than incrementals, so
  this ratio moves the byte rate substantially.
- **File descriptor ceiling** — the container caps a process at 20,000 open
  files, bounding one-connection-per-subscriber runs independently of CPU.
- **The generator is paced on a 1 ms tick**, so feed rates above 1,000 upd/s per
  instrument arrive in small bursts rather than evenly spaced.
- **`Grpc.Core` is the deprecated C-core binding.** The unicast per-message cost
  would likely improve substantially on `Grpc.AspNetCore`; the O(N) fan-out term
  would not.

---

## Reproducing

```bash
dotnet build MarketDataSimulator.sln -c Release
dotnet test Tests/Tests.csproj -c Release
./scripts/smoke.sh

# Order book micro-benchmark
dotnet run --project Bench -c Release -- books --depths 10,100,1000

# Unicast sweeps
python3 bench/run.py --subscribers 100 200 300 400 500 600 700 800 --rates 50 \
    --warmup 8 --duration 25 --tag feed100
python3 bench/run.py --subscribers 1000 2000 3000 4000 5000 --rates 5 \
    --warmup 8 --duration 25 --connect-batch 250 --connect-batch-delay-ms 20 --tag feed10

# Multicast sweep
python3 bench/run_multicast.py --subscribers 100 250 500 1000 2000 4000 6000 8000 \
    --rates 50 --warmup 6 --duration 20 --tag mcast

python3 bench/report.py feed100 feed10
```

`--rates` is per instrument, so `--rates 50` with two instruments is a 100 upd/s
feed. Both runners start a fresh server per case, so runs cannot contaminate each
other; raw per-run JSON lands in `bench/results/`.

---

## Where the remaining headroom is

1. **Move the unicast path off `Grpc.Core`.** The C-core binding has been
   unsupported since 2022 and its per-write interop cost is the largest single
   term in the unicast server's CPU budget.
2. **Measure multicast with subscribers on separate hosts.** It is the only way
   to separate the publisher's flat cost from the kernel's local replication, and
   the only way to observe real packet loss driving the recovery machinery.
3. **Kernel bypass** (`AF_XDP`, `io_uring`, or a userspace stack) for the
   publisher, once the transport is no longer the bottleneck.
4. **Encode once, write bytes N times** on the unicast path — encoding is already
   once per update, but each stream re-serialises the message object.
