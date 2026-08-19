# Dissemination benchmark

Measured capacity of the exchange-to-client broadcast path in this repository,
over two transports: per-subscriber gRPC streams, and sequenced UDP multicast.

Every number below was produced by the harness in this repo, on the machine
described under [Environment](#environment), and the raw per-run JSON is in
`bench/results/`.

**Every table here is generated from those JSON files** by `bench/docgen.py`, and
CI fails the build if any of them has drifted from the file it came from. The
prose between the tables is written by hand, and is the part to read sceptically.

## Contents

- [Headline](#headline)
- [What is being measured](#what-is-being-measured)
- [Environment](#environment)
- [Unicast: the O(N) wall](#unicast-the-on-wall)
- [Multicast: removing the term](#multicast-removing-the-term)
- [Batching: why fan-out inverts the usual trade](#batching-why-fan-out-inverts-the-usual-trade)
- [Head to head](#head-to-head)
- [Validation against real NASDAQ market data](#validation-against-real-nasdaq-market-data)
- [Order flow imbalance on real data](#does-the-book-predict-anything-order-flow-imbalance-on-real-data)
- [Order book micro-benchmark](#order-book-micro-benchmark)
- [Matching engine micro-benchmark](#matching-engine-micro-benchmark)
- [A lock-free queue, and why it did not help](#a-lock-free-queue-and-why-it-did-not-help)
- [Does the simulator look like a market?](#does-the-simulator-look-like-a-market-stylized-facts)
- [Repeatability](#repeatability)
- [Threats to validity](#threats-to-validity)
- [Reproducing](#reproducing)
- [Where the remaining headroom is](#where-the-remaining-headroom-is)

---

## Headline

On a 4 vCPU host with the load generator running **on the same box**, with zero
dropped updates, zero detected gaps, and every subscriber receiving the complete
feed:

<!-- generated: headline -->
| Transport | Highest sustained subscribers | Is that a ceiling? | Fan-out at that point | Mean latency | p99 | Server CPU |
|---|---|---|---|---|---|---|
| Unicast gRPC | 900 | top of sweep, not a limit | 86,621 msg/s | 46.15 ms | 216.7 ms | 229.4% |
| **Multicast** | **6,000** | ceiling (next point up failed) | **594,067 msg/s** | **34.39 ms** | **184.7 ms** | **73.2%** |
<!-- /generated -->

Read the second column carefully. Multicast's 6,000 is a real ceiling — 8,000 was
measured and failed. Unicast's 900 is simply the largest population this sweep
ran; every unicast point sustained, so its limit was not found and is somewhere
above 900. The unicast figure is therefore a floor on its capability, not a cap,
and the comparison below is correspondingly conservative in multicast's favour
only on *latency*, where the measurements are matched.

At matched subscriber counts:

<!-- generated: head-to-head -->
| Subscribers | Unicast mean | Multicast mean | Improvement |
|---|---|---|---|
| 100 | 1.61 ms | **0.31 ms** | **5.3×** |
| 500 | 8.54 ms | **0.85 ms** | **10.1×** |

Multicast was also measured at 250, 1,000, 2,000, 4,000, 6,000, 8,000 subscribers, where unicast was not run; those points are in the multicast sweep in BENCHMARKS.md.
<!-- /generated -->

Peak measured throughput was **972,472 messages/second** delivered to 1,000
multicast subscribers at 2.18 ms mean latency, with the server at under half a
core.

The single most important measurement in this document is not a latency figure.
It is that the server transmitted **98.6 to 100.3 packets per second in every
sustained multicast run**, from 100 subscribers to 6,000 — the update rate,
entirely independent of the audience.

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
hit its configured rate — in the sweeps below it delivers 96–98% of what was
asked for — and a ratio computed against the nominal rate books that
shortfall as though subscribers had lost data. Those are opposite failures: one
is the system under test failing, the other is the measuring instrument falling
short, and a single number that cannot tell them apart is not evidence about
either. They are reported as separate columns, `Delivered` and `Gen. rate`, and
only the first is part of the sustained criterion.

Both are quotients of two rates sampled independently — one by the subscriber
processes, one by the server's own periodic telemetry — so `Delivered` lands a
little either side of 100% even when nothing was lost. It is not clamped, because
a column that cannot read above 100% hides its own error bar.

Under multicast the criterion is stricter than that ratio, not looser. The feed
is sequenced, so a subscriber that misses a message *detects* it and reports a
gap; `Gaps` and `Missed` are direct counts of loss, while `Delivered` is a
quotient of two rates sampled independently by two processes and carries the
noise of both. A multicast run is judged on the counters. Where the two
disagree — a run with zero gaps whose quotient rounds just under the bar — the
counted gap is the better evidence, and a shortfall large enough to matter shows
up in both columns anyway.

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

| | |
|---|---|
| Transports | gRPC over HTTP/2 cleartext; UDP multicast on 239.7.7.7 — both over loopback |
| Instruments | 2, depth 10, 5% of updates are full snapshots |
| Per run | 8 s warm-up discarded, 20–30 s measured, fresh server process per case |

The host row is recorded by `bench/environment.py` when the measurements are
taken, not probed when the document is generated — otherwise a table would be
silently relabelled with the CPU of whoever last ran the generator. Each run also
stamps the kernel instance it executed on, and `docgen.py` refuses to render a
document from results that disagree about it. That check exists because these
measurements run in a container that can be replaced between phases of a session,
and an earlier revision of this file did quote two different hosts as one.

---

## Unicast: the O(N) wall

Feed rate held constant at 100 updates/s aggregate; subscriber population varied.

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

Every point here sustained, so this sweep does not contain unicast's breaking
point — but it contains the wall plainly enough. Work per second and latency rise
together *by construction*, since the fan-out is `subscribers × feed rate`; what
matters is that the server's own CPU rises from 53% to 229% of 400% to do it,
and host CPU reaches 379%. At 900 subscribers the box has roughly one core of
headroom left and the latency distribution has begun to come apart — p99 of
216.7 ms against a mean of 46.2 ms. The mechanism is not in doubt even though the
cliff was not reached.

The `Gen. rate` column is worth a glance: the update generator delivers 96–98% of
its configured rate throughout. That shortfall is the harness, not the server,
which is precisely why `Delivered` is computed against what was published rather
than against what was requested.

A second sweep holds the *message rate* constant instead, on a lighter feed:

<!-- generated: unicast-sweep-light -->
| Subscribers | Fan-out (msg/s) | Mean (ms) | p50 | p99 | p99.9 | Max | Delivered | Gen. rate | Server CPU | Host CPU | Sustained |
|---|---|---|---|---|---|---|---|---|---|---|---|
| 1,000 | 10,100 | **18.63** | 18.05 | 50.0 | 61.1 | 61.6 | 100.0% | 101% | 42.8% | 65.1% | yes |
| 2,000 | 19,868 | **34.97** | 35.15 | 77.3 | 137.2 | 137.9 | 103.5% | 96% | 68.7% | 121.1% | yes |
| 3,000 | 30,100 | **55.35** | 49.95 | 223.2 | 264.4 | 291.9 | 100.3% | 100% | 108.9% | 198.3% | yes |
| 4,000 | 39,998 | **69.22** | 65.25 | 180.2 | 289.6 | 357.2 | 100.0% | 100% | 134.1% | 253.9% | yes |
| 5,000 | 50,334 | **96.83** | 87.95 | 321.1 | 387.1 | 422.2 | 100.7% | 100% | 157.4% | 304.2% | yes |
<!-- /generated -->

### What the two sweeps say together

Put one row from each beside the other — matched on messages per second, so the
only thing that differs is how many subscribers that work is spread across:

<!-- generated: equal-work -->
| Subscribers | Feed rate | Fan-out | Mean latency |
|---|---|---|---|
| 100 | 100 upd/s | 9,490 msg/s | **1.61 ms** |
| 1,000 | 10 upd/s | 10,100 msg/s | **18.63 ms** |
<!-- /generated -->

Identical work per second. Ten times the audience costs an order of magnitude
more latency, and neither run was CPU-starved — so this is not saturation. It is
the fan-out span: every update is written once per subscriber, and a subscriber's
latency is essentially its position in that sequence of writes.

**Throughput** is bounded separately, by CPU cost per message:

<!-- generated: cost-per-message -->
| Transport | Highest sustained subscribers | Messages/s | Server CPU | Server CPU per message |
|---|---|---|---|---|
| Unicast gRPC | 900 | 86,621 | 229.4% | **26.48 µs** |
| Multicast | 6,000 | 594,067 | 73.2% | **1.23 µs** |

Multicast delivers each message for **21× less server CPU**, to **6.7× the subscribers** at **6.9× the throughput**.
<!-- /generated -->

The dominant term in the unicast figure is the per-write trip through the
`Grpc.Core` C-core interop layer. Notice also that the unicast server never
exceeds about 230% of 400%: it cannot use the whole machine, because
dissemination is serialised behind a fan-out that must visit every subscriber in
turn. Adding cores does not fix an O(N) term either.

### Before the fan-out rework

The original implementation awaited each subscriber's network write *inside* the
broadcast loop, rebuilt the wire message once per subscriber, and allocated a
`HashSet` per subscriber per update merely to test subscription. Measured then:
82 ms mean at 200 subscribers, 5,240 ms at 300, 7,953 ms at 400, with fan-out
flat at roughly 20,000 msg/s across all three — a rate that does not rise while
latency grows without bound is the signature of a queue that never drains. Its
best sustained result was 150 subscribers.

Those figures are quoted from the run that motivated the rework and are **not**
reproducible from this tree: the code they measured has been deleted, and they
were taken on an earlier revision. They are kept because the shape of the failure
is the point — unbounded latency growth at a pinned throughput ceiling — not
because the digits are comparable with the sweep tables above, which they are
not. Compare orders of magnitude only.

The rework — encode once per update; lock-free subscription test against a
copy-on-write snapshot; a bounded queue per subscriber drained by its own pump,
so the broadcast thread never touches the network — moved the collapse point from
roughly 200 subscribers to beyond the top of the sweep above. It did **not**
remove the O(N) term, because nothing at this layer can.

---

## Multicast: removing the term

Same matching engine, same feed rate, same measurement. The publisher encodes
each update once and sends a single datagram; the network performs the
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

**The `Server pkt/s` column is the result.** It does not move: 98.6 to 100.3
packets per second from 100 subscribers to 6,000. The publisher transmits at the
update rate and has no idea how many subscribers exist —
`MulticastOrderbookService` contains no subscriber table at all.

Every sustained run: zero gaps, zero missed messages, zero stale subscribers,
zero malformed packets.

**8,000 is where it breaks, and it breaks the way an unreliable transport
should.** 594 sequence gaps, 91% delivery, and a mean latency two orders of
magnitude worse — but detected, reported by every affected subscriber, and
recovered from at the next periodic snapshot rather than silently corrupting
anybody's book. That failure mode is the entire reason the sequencing machinery
exists, and the run is left in the table rather than trimmed off the end of it.

Note also what the publisher does at that point: its packet rate *falls*, to
81.5/s. The publisher is not sending more and being lost; the host has run out of
capacity to generate and receive at that scale at all.

Server CPU rises from 14% to 73% across a 60× increase in audience, and that
residual is not fan-out work: on a single host the kernel replicates each
datagram into every subscriber's socket buffer, and some of that cost is charged
to the sender's softirq context. On a real network switches do that replication.
Host CPU — which includes every receiver — is what actually bounds this
measurement, not the server.

---

## Batching: why fan-out inverts the usual trade

Batching normally trades latency for throughput — you wait to accumulate work,
so each item leaves later. Measured on a fan-out feed, it does the opposite.

1,000 subscribers, 1,000 updates/s aggregate (≈1M messages/s delivered), varying
only how many messages the publisher packs into a datagram:

<!-- generated: batching -->
| Max batch | Fan-out (msg/s) | Mean (ms) | p99 | Server pkt/s | Server CPU | Host CPU |
|---|---|---|---|---|---|---|
| 1 | 866,035 | **3.32** | 14.3 | 890.0 | 78.1% | 377.7% |
| 4 | 971,437 | **2.25** | 5.6 | 299.6 | 51.2% | 244.5% |
| 16 | 972,472 | **2.18** | 5.2 | 199.9 | 46.7% | 172.3% |
| 64 | 971,792 | **2.09** | 4.7 | 201.0 | 45.0% | 170.9% |
<!-- /generated -->

Batching cut the packet rate by 4.4×, mean latency by 1.6×, p99 by 3.1×, and host
CPU by 2.2× — while *increasing* delivered throughput by 12%.

The reason is that per-packet cost in a broadcast system is not paid once — it is
paid once per subscriber. Every datagram costs the sender a syscall and costs
each of 1,000 receivers a wakeup, a copy and a trip through the socket layer.
Halving the packet count removes that work 1,000 times over. At `MaxBatch = 1`
the host is at 378% of 400% and messages queue behind saturated receivers, which
is where the extra latency comes from; batching removes the saturation,
and the latency goes with it.

Batching is not free of the usual trade — it is still bounded by the flush
deadline, and a genuinely idle feed would see a message wait for it. But when the
audience is large, the amortisation is multiplied by the audience and dominates.

Note the packet rates at `MaxBatch` 16 and 64 are effectively identical (≈200/s).
Neither is hitting its batch limit: the binding constraint is the 1 ms flush
deadline, which under load resolves closer to 5 ms because `Task.Delay` cannot do
better. A publisher that needed tighter control would spin or use a timer wheel
rather than the thread pool timer.

**A measurement bug worth recording, because it produced a table that looked like
a result.** An earlier run of this series varied `MaxBatch` while leaving the
flush interval at zero — and with no flush deadline the publisher sends every
update immediately, so the batch limit is never reached. The packet rate came out
at ≈880/s for every setting: the same configuration measured four times while
appearing to vary. It is a benign-looking flag combination that silently disables
the thing under test, so `run_multicast.py` now refuses it outright rather than
producing four identical rows with different labels.

---

## Head to head

<!-- generated: head-to-head -->
| Subscribers | Unicast mean | Multicast mean | Improvement |
|---|---|---|---|
| 100 | 1.61 ms | **0.31 ms** | **5.3×** |
| 500 | 8.54 ms | **0.85 ms** | **10.1×** |

Multicast was also measured at 250, 1,000, 2,000, 4,000, 6,000, 8,000 subscribers, where unicast was not run; those points are in the multicast sweep in BENCHMARKS.md.
<!-- /generated -->

Cost per delivered message, at each transport's sustained ceiling:

<!-- generated: cost-per-message -->
| Transport | Highest sustained subscribers | Messages/s | Server CPU | Server CPU per message |
|---|---|---|---|---|
| Unicast gRPC | 900 | 86,621 | 229.4% | **26.48 µs** |
| Multicast | 6,000 | 594,067 | 73.2% | **1.23 µs** |

Multicast delivers each message for **21× less server CPU**, to **6.7× the subscribers** at **6.9× the throughput**.
<!-- /generated -->

The latency curve still rises with subscriber count under multicast — 0.31 ms at
100 subscribers, 34.4 ms at 6,000 — but for a different reason than under
unicast. The publisher's cost is flat; what grows is the kernel's local
replication and the contention among thousands of co-resident receivers on four
cores. That is an artefact of measuring on one box, not a property of the design,
and it is the single strongest argument for repeating this on separate hosts.

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

<!-- generated: replay -->
| Symbol | Levels | Book | Transitions | Exact | Accuracy | Msg/s |
|---|---|---|---|---|---|---|
| AMZN | 10 | SortedArray | 269,747 | 269,747 | 100.0000% | 975,463 |
| AMZN | 10 | Vectorized | 269,747 | 269,747 | 100.0000% | 1,022,018 |
| AMZN | 10 | Ladder | 269,747 | 269,747 | 100.0000% | 205,476 |
| AMZN | 10 | Tree | 269,747 | 269,747 | 100.0000% | 341,968 |
| GOOG | 5 | SortedArray | 112,672 | 112,672 | 100.0000% | 1,672,643 |
| GOOG | 5 | Vectorized | 112,672 | 112,672 | 100.0000% | 1,508,026 |
| GOOG | 5 | Ladder | 112,672 | 112,672 | 100.0000% | 164,977 |
| GOOG | 5 | Tree | 112,672 | 112,672 | 100.0000% | 477,730 |
| MSFT | 5 | SortedArray | 595,799 | 595,799 | 100.0000% | 2,014,540 |
| MSFT | 5 | Vectorized | 595,799 | 595,799 | 100.0000% | 1,926,050 |
| MSFT | 5 | Ladder | 595,799 | 595,799 | 100.0000% | 642,539 |
| MSFT | 5 | Tree | 595,799 | 595,799 | 100.0000% | 506,222 |
<!-- /generated -->

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
cleared once at startup; ruinous when cleared per message, which is exactly what replaying
against published snapshots does. Both books now clear in time proportional to what they
actually hold rather than to the space they could hold: the ladder walks its occupancy index,
and `SortedArrayBook` is constant-time. The `Clear` column in the micro-benchmark above exists
because of this, and is the only column in this document that no synthetic workload would have
motivated.

**A structural weakness of the ladder.** It remains the slowest here, and for a reason worth
knowing: its bit-scan walks the price *band*, and a real book at $0.0001 granularity occupies
about 0.02% of a ten-dollar band. Sparse real prices are exactly the case a synthetic benchmark
with a tight band never produces.

### Parser

The LOBSTER CSV parser sustains **13.3–13.6M messages/sec at 528–532 MiB/s, and allocates zero
bytes** (measured on all three sessions; see the generated table above for the per-session
figures).
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

<!-- generated: microstructure -->
| Symbol | Bucket | Samples | Contemporaneous R² | t | Predictive R² | t |
|---|---|---|---|---|---|---|
| AMZN | 10 | 26,974 | 6.15% | 42.0 | 0.06% | 4.0 |
| AMZN | 25 | 10,789 | 8.44% | 31.5 | 0.38% | 6.4 |
| AMZN | 50 | 5,394 | 10.79% | 25.5 | 0.87% | 6.9 |
| AMZN | 100 | 2,697 | 14.69% | 21.5 | 0.71% | 4.4 |
| AMZN | 250 | 1,078 | 25.56% | 19.2 | 0.44% | 2.2 |
| AMZN | 500 | 539 | 30.66% | 15.4 | 0.00% | 0.1 |
| GOOG | 10 | 11,267 | 17.11% | 48.2 | 0.19% | 4.6 |
| GOOG | 25 | 4,506 | 23.14% | 36.8 | 1.53% | 8.4 |
| GOOG | 50 | 2,253 | 27.94% | 29.5 | 2.42% | 7.5 |
| GOOG | 100 | 1,126 | 31.27% | 22.6 | 1.24% | 3.8 |
| GOOG | 250 | 450 | 31.45% | 14.3 | 0.17% | 0.9 |
| GOOG | 500 | 225 | 28.90% | 9.5 | 0.10% | -0.5 |
| MSFT | 10 | 59,579 | 7.80% | 71.0 | 0.60% | 18.9 |
| MSFT | 25 | 23,831 | 20.35% | 78.0 | 1.30% | 17.7 |
| MSFT | 50 | 11,915 | 34.60% | 79.4 | 3.76% | 21.6 |
| MSFT | 100 | 5,957 | 50.72% | 78.3 | 7.29% | 21.6 |
| MSFT | 250 | 2,383 | 65.24% | 66.8 | 5.26% | 11.5 |
| MSFT | 500 | 1,191 | 70.69% | 53.5 | 1.58% | 4.4 |
<!-- /generated -->

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

<!-- generated: books-full -->
| Depth | Implementation | Mixed ns/op | Touch ns/op | Top-10 publish ns/op | Clear ns/op | Bytes per publish |
|---|---|---|---|---|---|---|
| 10 | SortedArrayBook | 23.6 | 1.8 | 5.3 | 20 | 0 |
| 10 | VectorizedBook | 17.8 | 4.4 | 13.7 | 27 | 0 |
| 10 | LadderBook | 17.5 | 7.5 | 39.8 | 101 | 0 |
| 10 | TreeBook | 38.9 | 9.6 | 102.1 | 46 | 104 |
| 32 | SortedArrayBook | 31.1 | 1.8 | 5.3 | 20 | 0 |
| 32 | VectorizedBook | 23.1 | 4.4 | 14.2 | 30 | 0 |
| 32 | LadderBook | 20.0 | 5.3 | 40.0 | 285 | 0 |
| 32 | TreeBook | 55.9 | 10.1 | 120.5 | 60 | 136 |
| 64 | SortedArrayBook | 33.8 | 1.8 | 5.3 | 20 | 0 |
| 64 | VectorizedBook | 27.0 | 4.4 | 13.5 | 32 | 0 |
| 64 | LadderBook | 20.1 | 5.5 | 38.8 | 550 | 0 |
| 64 | TreeBook | 62.0 | 10.3 | 121.8 | 106 | 152 |
| 128 | SortedArrayBook | 37.6 | 1.8 | 5.6 | 20 | 0 |
| 128 | VectorizedBook | 30.0 | 4.3 | 13.8 | 37 | 0 |
| 128 | LadderBook | 21.0 | 5.2 | 39.7 | 1,087 | 0 |
| 128 | TreeBook | 68.4 | 11.5 | 132.8 | 139 | 168 |
| 1,000 | SortedArrayBook | 49.0 | 1.8 | 5.3 | 43 | 0 |
| 1,000 | VectorizedBook | 40.3 | 4.4 | 13.7 | 167 | 0 |
| 1,000 | LadderBook | 27.0 | 5.4 | 40.2 | 9,122 | 0 |
| 1,000 | TreeBook | 94.5 | 13.4 | 141.0 | 825 | 200 |
<!-- /generated -->

Four results.

**1. The publish path is flat for the array and only for the array.** Copying the
top ten levels costs `SortedArrayBook` 5.3 ns at every depth from 10 to 1000,
because the top ten levels are the first ten elements of an array and the copy
does not care what follows them. Every other structure pays to *assemble* that
answer: the SIMD book re-interleaves prices and quantities held in separate
arrays (13.7 ns), the ladder walks its bitset one set bit at a time (≈40 ns), and
the tree chases pointers (102–141 ns, and rising with depth). The array is 2.6×
faster than the next best and up to 27× faster than the tree.

**2. On the update path the array is not the best at any depth.** This reverses
what an earlier revision of this document claimed, and the earlier claim was a
measurement artefact rather than a result — see the note below. The ladder wins
mixed updates everywhere measured, from 17.5 ns at depth 10 to 27.0 ns at depth
1000, because its update is O(1) with no comparisons and no data movement. The
array's O(d) shift is cheap but not free, and it grows: 23.6 ns at depth 10,
49.0 ns at depth 1000.

**3. So the choice of default is a trade, not a walkover.** At depth 10 the array
gives up ≈6 ns per update and gains ≈8.4 ns per publish against the SIMD book,
and gives up ≈6 ns to gain ≈34 ns against the ladder. A feed that publishes on
most updates comes out ahead with the array; one that absorbs many updates per
published snapshot would not. `SortedArrayBook` remains the default because this
feed publishes constantly — but the honest statement is that it wins the axis
this workload is dominated by, not that it wins outright.

**4. The tree allocates on every publish** — 104 to 200 bytes, growing with
depth, because enumerating a `SortedSet` allocates its traversal stack. On a
dissemination path that is not a throughput detail; it is a source of collection
pauses at arbitrary moments. Every other implementation publishes without
allocating.

The `Clear` column is the one real market data added, and it is the sharpest
separation in the table. Clearing is O(1) for the array — ≈20 ns whether it holds
10 levels or 1000 — and proportional to occupancy for the ladder, which is why it
reaches 9,122 ns at depth 1000 against the array's 43. Clearing looks like
start-up housekeeping until you replay a session against published snapshots,
which clears the book once per message.

One caveat on reading that column: this benchmark sizes the price band at four
times the depth, so band and occupancy grow together and the two cannot be
separated from these numbers alone. What they do show is that the ladder's clear
costs a roughly constant 4.6–5.0 ns per live level across a hundredfold range,
which is the signature of walking an occupancy index rather than the price band —
the behaviour the ladder was changed to have after real data exposed the
alternative. Distinguishing band-proportional from level-proportional
conclusively would need a sweep that varies the band at fixed depth, which this
harness does not currently do.

### A note on how these numbers were wrong before

An earlier revision of this table reported figures that were artefacts of the
measurement harness, and the artefacts were large enough to reverse conclusions —
so the correction is worth describing rather than quietly applying.

Whichever depth was measured **first** reported inflated costs for every
implementation except the one measured first within it, and the distortion
followed position in the sweep rather than depth: reversing the depth order moved
it from depth 10 to depth 1000. At depth 10 — the depth this feed actually ships
— it was large enough to make the array look dominant on the update path when it
is not, and to make the ladder's publish cost look 7× worse than it is.

Two mechanisms, both artefacts of a managed runtime rather than of the data
structures:

- Every measurement calls through `IOrderBook` from a **single shared call site**.
  The first implementation to reach it makes that site monomorphic, so the JIT
  devirtualizes and inlines for that type — and every implementation measured
  afterwards fails the resulting type guard on every call.
- Promotion to optimised code is **not purely call-count driven**. The
  compilation runs on a background thread, so a configuration can finish being
  measured while still executing unoptimised code. Per-measurement warm-up trials
  cannot fix this, because they do not buy wall-clock time for a compilation to
  land.

The fix is to run the entire sweep twice and record only the second pass, which
addresses both: by the time anything is recorded, every call site has seen every
implementation and every method body has long since been promoted. The same
correction applies to the matching benchmark, where the smallest book had been
reporting roughly twice its true cost per operation for the same reason.

This is the general hazard in micro-benchmarking a JIT-compiled language, and the
symptom to watch for is exactly the one here: a result that depends on
*measurement order*. It is worth re-running any such suite with its cases
reversed before believing it.

### The SIMD book, and where it actually wins

`VectorizedBook` replaces the binary search with a branch-free vector count, on
the theory that what costs a binary search at these sizes is not the comparisons
but the branch mispredictions — each step depends on the last, and the predictor
has nothing to work with on random prices. Two changes make that possible: prices
move into their own contiguous `int[]` (struct of arrays), and bids are inverted
so both sides ascend and a price's position is simply the count of keys below it.
Locating a price becomes one vector load, one compare, one mask extract, one
population count, on 16 lanes at a time under AVX-512.

On the update path it does what it was built to do: it beats the plain array at
every depth measured, by 25% at depth 10 and 18% at depth 1000. It does not beat
the ladder, whose O(1) update does not have to search at all.

What it costs is the publish path. Struct-of-arrays means the top ten levels are
not contiguous — prices live in one array and quantities in another — so
publishing has to re-interleave them element by element instead of issuing one
memory copy. That is the whole of the 5.3 ns versus 13.7 ns gap, and it does not
go away with better code, because it is the direct consequence of the layout that
makes the search fast.

The branch-free scan is also O(n) where a binary search is O(log n), so the
vector path is used only below a measured crossover and a binary search takes
over above it. That crossover is a tuning constant with a measurement behind it,
not a guess.

So the fastest search does not win, because search is not what a depth-10 market
data book spends its time on. The interesting result is not that SIMD is fast; it
is that it was measured, found to win an axis this workload is not dominated by,
and left switchable rather than adopted.

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

<!-- generated: matching -->
| Resting orders | Add | Cancel | Match | Mixed (60/35/5) | Bytes/op |
|---|---|---|---|---|---|
| 100 | 74.5 ns | 112.9 ns | 108.6 ns | 107.3 ns | 39.8 |
| 1,000 | 71.2 ns | 116.6 ns | 123.1 ns | 113.8 ns | 40.8 |
| 10,000 | 61.9 ns | 130.1 ns | 130.1 ns | 129.7 ns | 38.0 |
| 100,000 | 64.5 ns | 250.9 ns | 141.0 ns | 96.7 ns | 15.3 |
<!-- /generated -->

Mixed is 60% add, 35% cancel, 5% aggressive — roughly a real venue's message profile, where
cancels vastly outnumber trades.

**Add is flat across a thousandfold range** — 74.5 ns at a hundred resting orders, 64.5 ns at a
hundred thousand — which is the O(1) claim surviving contact with a measurement rather than being
asserted from the code.

**Cancel is flat from 100 to 10,000 resting orders and then roughly doubles, to 251 ns at a
hundred thousand.** The algorithm did not change; the working set did. At ten thousand orders the
id map and the order objects together are on the order of a megabyte and sit in cache. Ten times
larger, every cancel becomes two dependent trips towards main memory — the map lookup, then the
order object it points at. The knee lands where the working set leaves the last-level cache.

This is the same lesson as the aggregated book benchmark, in the other direction: complexity
classes describe how cost scales with *n*, and say nothing about what a constant-time operation
actually costs once *n* stops fitting in cache. An order book sized for a real venue lives on the
right-hand side of that table, which is an argument for compact, pooled, contiguous storage rather
than for a cleverer algorithm.

An earlier revision of this table reported the 100-order row as roughly twice as slow as the
1,000-order row, and explained it with a story about level churn at small book sizes. The story
was plausible and wrong: the first configuration measured was finishing before the JIT had
promoted its code to an optimised tier, so it was paying for the compilation on everyone else's
behalf. The benchmark now discards a full warm-up pass, and the row falls into line. It is worth
saying because the wrong explanation was the more interesting one, which is exactly what makes
that failure mode dangerous.

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

<!-- generated: queue -->
| Queue | ns/item | M items/s | Bytes/item |
|---|---|---|---|
| RingBuffer (single thread) | 2.4 | 409.8 | 0 |
| Channel (single thread) | 47.9 | 20.9 | 0 |
| RingBuffer (producer + consumer) | 6.3 | 157.5 | 0 |
| RingBuffer batched (prod + cons) | 4.0 | 248.8 | 0 |
| Channel (producer + consumer) | 151.2 | 6.6 | 0 |
<!-- /generated -->

**Decisively faster in isolation. It made no measurable difference end to end.**

Wired into the dissemination path behind `UseRingQueue` and measured at 500 subscribers, three
runs each:

<!-- generated: queue-ab -->
| Dissemination queue | Run means (ms) | Mean of means | Spread |
|---|---|---|---|
| Channel | 7.73, 8.64, 8.66 | 8.34 | 0.93 |
| Ring buffer | 7.63, 8.55, 9.46 | 8.55 | 1.83 |
<!-- /generated -->

The spread *within* each option is comparable to the gap *between* them, so the honest reading is
*no difference* — not *the ring is worse*, and not *the ring is better*. Three runs cannot resolve
a difference smaller than their own scatter, and pretending otherwise is how a null result gets
written up as a win.

The arithmetic says why, and it is worth doing before optimising rather than after:

<!-- generated: queue-arithmetic -->
| | |
|---|---|
| Concurrent hand-off, channel | 151.2 ns/item |
| Concurrent hand-off, ring | **6.3 ns/item** |
| Ring speed-up in isolation | **23.8×** |
| Rate this queue actually carries | 97 updates/s (not the 48,500 msg/s of fan-out) |
| Time saved per second of running | **14.1 µs**, or **0.0014% of one core** |
<!-- /generated -->

The hand-off sits **upstream of fan-out**. It carries the *update* rate, not the *message* rate,
because one update becomes 500 messages only after it leaves the queue. No queue implementation
could have mattered there: the cost the server actually pays is in the gRPC write path, which is
downstream and runs 500× more often.

The ring stays in the repository, switchable and defaulted off, because the measurement is the
point: it is a correct, tested, genuinely faster component in the wrong place. The lesson is
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

Each configuration was run three times, with a fresh server process each time.

<!-- generated: repeatability -->
| Point | Runs | Median | Min | Max | Spread |
|---|---|---|---|---|---|
| 4,000 subscribers, 10 upd/s | 3 | 47.18 ms | 43.25 | 60.35 | 1.40× |
| 500 subscribers, 100 upd/s | 3 | 8.64 ms | 7.73 | 8.66 | 1.12× |
<!-- /generated -->

The 4,000-subscriber spread is the important caveat: a 1.4× range across three
runs of an identical configuration. At that population the result is sensitive to
how 4,000 connections happen to land across threads and garbage collection, and a
single sweep point does not characterise it. The 500-subscriber point is stable
enough to quote directly.

Read every figure at high subscriber counts as carrying tens of percent of
run-to-run variation unless it has repeat runs behind it — and note that this
cuts against the sweep tables above, every point of which is a single run.

---

## Does the simulator look like a market? Stylized facts

Reconstructing a real session exactly proves the *book* is right. It says nothing about whether
the *feed* the simulator generates resembles a market, and that is a separate question with a
separate answer.

Financial return series have well-known distributional signatures that a random walk does not:
returns are strongly leptokurtic (far more extreme moves than a normal distribution allows), and
volatility clusters (large moves follow large moves, so the *absolute* returns are
autocorrelated even though the returns themselves are not). Both are measured here on mid-price
returns sampled every 20 book updates, for the three real sessions and for the simulator's own
output.

<!-- generated: realism -->
| Series | Observations | Excess kurtosis | \|r\| ac(1) | \|r\| ac(10) | >3σ | >5σ |
|---|---|---|---|---|---|---|
| AMZN (real) | 13,487 | 23.49 | 0.1793 | 0.1149 | 1.690% | 0.289% |
| GOOG (real) | 5,633 | 24.20 | 0.2262 | 0.0915 | 1.207% | 0.373% |
| MSFT (real) | 29,789 | 14.53 | 0.0253 | 0.0391 | 0.940% | 0.940% |
| simulator | 19,999 | 7,969.82 | 0.2244 | 0.2808 | 0.620% | 0.055% |
<!-- /generated -->

The real sessions behave exactly as the literature says they should: excess kurtosis in the
teens to twenties against a normal distribution's zero, positive absolute-return autocorrelation
that persists to a lag of ten, and essentially no autocorrelation in the signed returns. Nothing
here is a new result — the point is that the measurement reproduces the known one, which is what
makes it usable as a yardstick.

**Against that yardstick the simulator fails, and not narrowly.** Its excess kurtosis is three
orders of magnitude too large, while it puts *less* mass beyond three and five sigma than the
real sessions do. Those two facts together describe a very specific shape: a nearly static price
punctuated by rare enormous jumps. The standard deviation is dominated by the jumps, so almost
everything else sits well inside one sigma and the tail counts come out low, while the fourth
moment explodes. A market's fat tails are not that; they are a continuum of moderately large
moves.

This is worth stating plainly because it bounds what the rest of this document means. The
dissemination measurements are measurements of a dissemination path under a given message rate
and message mix, and those hold regardless — the transport does not care whether the prices are
plausible. But the simulator is a load generator, not a market model, and any result that
depended on the *distribution* of the prices rather than on their rate would not transfer. The
microstructure section above is deliberately run on real LOBSTER data for exactly this reason.

Volatility clustering is the one signature the simulator does show — absolute-return
autocorrelation comparable to the real sessions — which is an artefact of the order flow
generator's own bursts rather than evidence of anything deeper.

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

# Micro-benchmarks
dotnet run --project Bench -c Release -- books    --depths 10,32,64,128,1000 --out bench/results/books.json
dotnet run --project Bench -c Release -- matching --out bench/results/matching.json
dotnet run --project Bench -c Release -- queue    --out bench/results/queue.json

# Real data (needs ./scripts/fetch-lobster.sh first)
dotnet run --project Bench -c Release -- replay  --out bench/results/replay.json
dotnet run --project Bench -c Release -- study   --out bench/results/microstructure.json
dotnet run --project Bench -c Release -- realism --out bench/results/realism.json

# Unicast sweeps
python3 bench/run.py --tag scale50 --rates 50 --subscribers 100 200 300 400 500 600 700 800 900
python3 bench/run.py --tag scale5  --rates 5  --subscribers 1000 2000 3000 4000 5000

# Multicast sweep, and the batching series
python3 bench/run_multicast.py --tag mcast --rates 50 \
    --subscribers 100 250 500 1000 2000 4000 6000 8000
for b in 1 4 16 64; do
  python3 bench/run_multicast.py --tag batch$b --max-batch $b --rates 500 --subscribers 1000
done

# Dissemination queue A/B, and repeatability - three runs each, because one run of
# anything at these subscriber counts characterises nothing
for i in 1 2 3; do
  python3 bench/run.py --tag qchannel$i --rates 50 --subscribers 500
  python3 bench/run.py --tag qring$i --ring --rates 50 --subscribers 500
  python3 bench/run.py --tag rep$i --rates 5 --subscribers 4000
done

# Rewrite every generated table in README.md and BENCHMARKS.md from the results
python3 bench/docgen.py --write
```

`--rates` is per instrument, so `--rates 50` with two instruments is a 100 upd/s
feed. Both runners start a fresh server per case, so runs cannot contaminate each
other; raw per-run JSON lands in `bench/results/`.

**Every table in this document and in the README is generated.** They sit between
`<!-- generated: name -->` markers and are written from `bench/results/*.json` by
`bench/docgen.py`; CI runs `docgen.py --check` and fails the build if any of them
has drifted from the file it came from. This is not tidiness. An audit of an
earlier revision found a published figure that disagreed with its own results file
by a factor of six, in the direction that flattered the argument the surrounding
paragraph was making - which is the one direction a reader cannot afford to
tolerate. The prose outside the markers is written by hand and is the part worth
reading sceptically.

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
5. **NativeAOT.** Everything targets `net8.0`, so it is available, and the
   interesting questions are start-up time and whether removing tiered
   compilation changes steady-state latency at the tail. Listed here rather than
   above because it has not been measured — and an unmeasured optimisation has no
   place in a table of measured ones.

### A note on what is *not* claimed

Two things this document deliberately does not assert.

It does not claim these are good absolute numbers. They are numbers from one
4-vCPU host with the load generator sharing it, and the only comparisons worth
anything here are the ones held against each other on the same box in the same
session: unicast against multicast, one book against another, the ring against
the channel.

It does not claim the simulator produces realistic prices. It does not — the
stylized-facts section above measures exactly how badly, on the simulator's own
output. Every microstructure result in this document is therefore computed on
real NASDAQ data, and the simulator is used only where what matters is the
*rate* and *shape* of messages rather than the plausibility of the prices they
carry.
