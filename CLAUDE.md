# Working in this repository

Conventions that are not obvious from the code, and the reasons behind them. Most
exist because something went wrong once.

## Build, test, verify

```bash
dotnet build MarketDataSimulator.sln -c Release
dotnet test  Tests/Tests.csproj -c Release --no-build
python3 bench/docgen.py --check
./scripts/smoke.sh
```

All four must pass before a commit. CI runs the same set plus a hardware-path matrix.

## The documentation is generated, and enforced

Tables in `README.md`, `BENCHMARKS.md` and `PORTING.md` sit between
`<!-- generated: name -->` markers and are written by `bench/docgen.py` from
`bench/results/*.json`. **Never hand-edit a table.** Edit the prose around it, or the
generator that produces it.

`docgen.py --check` fails the build if any table has drifted. This exists because an
audit found a published figure that disagreed with its own results file by a factor of
six — and disagreed in the direction that flattered the argument the paragraph was
making.

## Benchmark results are host-stamped

Every run records the kernel instance it executed on. `docgen.py` refuses to render a
document whose results disagree about the host, because these benchmarks run in
containers that can be replaced mid-session, and an earlier revision did quote two
machines as one.

Consequences:

- Run `python3 bench/environment.py` **before** re-measuring on a new machine.
- Never hand-merge result files from two machines.
- Refresh a generation as a whole. A partial re-run mixes hosts within one generation
  and is rejected.
- `v2`/`protocolv2` files are the current protocol record; unsuffixed files are the
  earlier transport generation. **A number from one never appears in the same sentence
  as a number from the other.**

## Benchmarks discard a warm-up pass

Both micro-benchmarks run their whole sweep twice and record only the second. This is
not superstition. Measurement order was distorting results badly enough to reverse a
conclusion, from two causes:

- Every measurement calls through an interface from one shared call site, so the first
  implementation to reach it makes the site monomorphic and the JIT devirtualizes for
  that type. Everything measured afterwards fails the resulting type guard.
- Promotion to optimised code happens on a background thread, so a configuration can
  finish measuring while still running unoptimised. Per-measurement warm-up trials
  cannot fix this — they do not buy wall-clock time for a compilation to land.

**If you add a benchmark, follow the pattern, and re-run any suite with its cases
reversed before believing it.** A result that depends on measurement order is not a
result.

## Claims are asserted, not documented

If a doc comment says something is allocation-free, exact, or bounded, a test asserts
it. `Tests/AllocationTests.cs` enforces zero bytes on the matching, publishing,
encoding and risk paths.

A claim nothing checks is a claim that quietly stops being true. This has already
happened once here: `PreTradeRiskGate` documented itself as allocation-free "which
AllocationTests asserts", and AllocationTests did not.

## Testing style

Roughly in order of how much they have caught:

- **Differential** — every book implementation runs the same operations and is compared
  after each one, so a divergence names the operation that caused it.
- **Model-based** (`Tests/ModelBasedTests.cs`) — a naive reference model runs alongside
  the real thing, compared after every operation, with shrinking.
- **Fuzz** (`Tests/FuzzTests.cs`) — anything parsing bytes it did not produce. Failures
  report a reproducing seed.
- **Exhaustive sweeps** where the space is small enough: every single-bit flip and every
  truncation length of a record or packet.
- **Chaos drills** — kill the writer mid-record, corrupt storage, ship a live journal.
- **Real data** — NASDAQ's own published book, per message.

Property tests take a seed and print it on failure. Reproduce with the printed seed
rather than re-running until it fails again.

## Things that are easy to get wrong here

- **`STATS` output format is parsed** by `bench/run.py`. Adding a field breaks the
  measurement record silently. Put new output on its own line.
- **The generated order stream must not change.** Transport numbers were measured
  against it. The risk layer is on that path and only leaves it unchanged while limits
  admit every order — asserted event-for-event in `RiskOnThePathTests`. Tightening a
  limit is a different experiment, not a tuning knob.
- **Two accounts drive the simulator**, one per side. A single account makes every match
  a self-trade, which the risk layer correctly refuses, and the book fills with orders
  that can never execute.
- **`Flush(true)`, not `Flush()`**, is what makes a journal durable. The latter only
  moves bytes into the OS.
- **Sequence numbers belong to the message stream.** Audit and checkpoint records
  annotate a point in it; they never consume one.
- **A duplicated invariant is a bug with a delay fuse.** Two independent copies of the
  same arithmetic have caused two separate defects here. Both were fixed by deleting the
  duplication, not by patching the second copy.

## Where the boundary is

`PORTING.md` lists what this codebase does not do and what could not be measured in a
container — multi-host measurement, PTP, hardware timestamps, kernel bypass, consensus
for epoch allocation, quorum commit. Keep that list honest. A boundary that quietly
moves is worse than one that stays put.
