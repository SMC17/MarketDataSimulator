# Porting this to real hardware

Everything in this repository was built and measured inside a shared 4-vCPU container.
That environment is honest about a lot and capable of surprisingly much, but several
things in the design are deliberately *unmeasured* here rather than small, and a few
are unimplementable rather than unimplemented.

This document is the handover: what the sandbox could not do, what to run first
somewhere real, and which recorded numbers stop being true the moment the host
changes.

## The host everything was measured on

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

Shared, virtualized, and not isolated. CPU frequency, core pinning, interrupt routing
and NIC behaviour were not controlled. Treat every absolute number as a property of
this box, and only the *comparisons made against each other on it* as portable.

## First run on the new machine

```bash
dotnet restore MarketDataSimulator.sln
dotnet build MarketDataSimulator.sln -c Release --no-restore
dotnet test  Tests/Tests.csproj -c Release --no-build --no-restore
./scripts/smoke.sh
```

That establishes the invariants still hold. Nothing about performance is claimed until
the next step.

### Re-measure before quoting anything

The benchmark tables in `README.md` and `BENCHMARKS.md` are generated from
`bench/results/*.json`, and CI fails if they drift. **Regenerating them on a new host
is not optional if you intend to quote them** — every run stamps the kernel instance it
executed on, and `bench/docgen.py` refuses to render a document whose results disagree
about the host. That check exists because an earlier revision of the docs did quote two
different machines as one.

```bash
python3 bench/environment.py          # stamp the new host first

dotnet run --project Bench -c Release --no-build -- protocol   --out bench/results/protocol-v2.json
dotnet run --project Bench -c Release --no-build -- matching   --out bench/results/matching-v2.json
dotnet run --project Bench -c Release --no-build -- queue      --out bench/results/queue-v2.json
dotnet run --project Bench -c Release --no-build -- books      --depths 10,32,64,128,1000 --out bench/results/books.json
dotnet run --project Bench -c Release --no-build -- durability --out bench/results/durability-v2.json
dotnet run --project Bench -c Release --no-build -- risk       --out bench/results/risk-v2.json
dotnet run --project Bench -c Release --no-build -- timing     --out bench/results/timing-v2.json

python3 bench/docgen.py --write
```

The full transport sweeps and their reproduction commands are in
`BENCHMARKS.md` under *Reproducing*. They take roughly an hour and must be run as a
whole: a partial refresh mixes hosts inside one generation and is rejected.

### Real market data

`data/sample/` holds a committed 20,000-message slice so CI validates offline. The full
sessions are ~185 MiB and not committed:

```bash
./scripts/fetch-lobster.sh
dotnet run --project Bench -c Release -- replay --data data/lobster
```

## What this environment could not do

These are the gaps worth attacking first on real hardware, in rough order of value.

### 1. Multi-host measurement — the big one

Everything runs over loopback with the load generator on the same box. That has two
consequences the numbers cannot escape:

- **The unicast ceiling is the box, not the server.** Near the top of the range the
  harness consumes about as much CPU as the server. The measured 900-subscriber
  unicast figure is the top of the sweep, not a limit — no unicast point failed.
- **Multicast replication is local.** The kernel copies each datagram into every
  subscriber's socket buffer, work switches would do on a real network. The
  publisher's cost is genuinely flat; the *host* cost is not, and the latency curve
  reflects it.

Put subscribers on separate machines and both distortions disappear. This is the single
measurement that would most change what the project can claim.

### 2. Clocks: PTP, hardware timestamps

`Common/Time/UncertainInstant` already carries an error bound and refuses to order two
instants whose bounds overlap. What is missing is a source good enough to make the
bound small:

- **PTP** needs a grandmaster and `ptp4l`/`phc2sys`. Once present, read the servo's
  offset and error and construct the clock with `TimestampSource.PtpDisciplined`.
- **Kernel timestamps** (`SO_TIMESTAMPING`) need a socket option this code does not yet
  set; that is a small, well-defined change to `MulticastSubscriber`.
- **NIC hardware timestamps** need the NIC and driver support.

The interface is built so these slot in — consumers already handle a source that admits
error. Until then, cross-host one-way latency is **not a claim this codebase supports**,
and the server says so in its `ENV` line at start-up.

### 3. Kernel bypass

`AF_XDP`, `io_uring`, or a userspace stack. Needs privileges and NIC support the
container does not grant. Deliberately unevaluated rather than half-claimed. Measure the
syscall cost first — it is what would justify the work — and only then decide.

### 4. Consensus for epoch allocation

Failover fencing is only as good as the uniqueness of the epoch token, and that cannot
be produced locally. `IEpochAllocator` is the seam; `InMemoryEpochAllocator` is correct
for a single process and useless across machines. Back it with etcd, ZooKeeper, or a
database sequence and the fencing story becomes real. Everything else in
`FailoverCoordinator` — lease expiry, the catch-up bar, epoch rejection on the receive
path — already works and is tested.

### 5. Quorum commit

Replication here is journal shipping, so a failover loses whatever had not been shipped.
That loss is measured and reported (`JournalShipper.Measure` returns a real RPO), not
assumed to be zero. Real quorum commit — acknowledge only once N replicas hold the
record — is a genuine piece of work and the natural sequel to (4).

### 6. CPU pinning was measured and found not to help *here*

Zero migrations in two seconds of busy looping, one NUMA node, and a difference inside
run-to-run noise. That verdict is host-specific and should be re-run: on a multi-socket
machine with isolated cores the answer may well invert. `Bench -- timing` prints a
verdict, not just a table.

## Repository conventions worth keeping

- **Docs are generated.** Tables live between `<!-- generated: name -->` markers and come
  from `bench/docgen.py`. Edit the prose, never the tables. CI runs `docgen.py --check`.
- **Results are host-stamped.** Do not hand-merge result files from two machines; the
  generator will reject them and it is right to.
- **Allocation claims are asserted.** `Tests/AllocationTests.cs` enforces zero bytes on
  the matching, publishing, encoding and risk paths. A claim nothing checks is a claim
  that quietly stops being true.
- **Benchmarks discard a warm-up pass.** Both micro-benchmarks run their whole sweep
  twice and record only the second. This is not superstition: measurement order was
  distorting results badly enough to reverse a conclusion, because of JIT
  devirtualization at a shared interface call site and background tier-1 compilation
  landing after the first configuration finished. If you add a benchmark, follow the
  pattern, and re-run any suite with its cases reversed before believing it.

## One thing to fix in GitHub

The repository's default branch is still `master`; a `main` branch exists at an older
commit and can be deleted. Neither could be changed from the sandbox — the agent proxy
blocks repository-settings and branch-deletion writes. Both are single clicks in
**Settings → General** and **Branches**.
