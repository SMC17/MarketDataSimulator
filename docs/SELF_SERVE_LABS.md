# Three market-data labs

Use .NET 10. Build once, then choose a task:

```sh
dotnet build MarketDataSimulator.sln -c Release
dotnet test Tests/Tests.csproj -c Release --no-build --no-restore
```

## 1. Follow one matching workload

```sh
dotnet run --project Bench -c Release --no-build -- matching --sizes 1000
```

Compare the add/cancel, matching and risk-path workloads. Treat timings as
observations on your machine. They do not reproduce a published benchmark merely
because the command finishes. The committed benchmark tables retain their
original host/runtime record.

## 2. Inspect packet-integrity work

```sh
dotnet run --project Bench -c Release --no-build -- protocol --iterations 10000 --trials 3
```

Follow encoding, sealing and validation across the protocol workloads. A fast
CRC loop is not evidence of correct network recovery, cross-host latency or a
production feed. The tests separately exercise malformed packets and state.

## 3. See what finite depth cannot tell you

```sh
dotnet run --project Bench -c Release --no-build -- replay --data data/sample --trials 3
```

The single-step task seeds from an exchange row, applies one message and checks
the determined next state. Cumulative replay seeds once and is a separate
observability study: orders below the published depth window are unknown.

In the measured .NET 10 macOS ARM64 run, the AMZN example's four implementations
matched all 19,999 comparable single-step transitions. Its cumulative stream
still diverged later. The command's success criterion is the single-step
comparison, not the claim that cumulative reconstruction is exact. Read the
separate tables and the first-mismatch explanation. Do not label missing
observability as parser correctness or silently omit the cumulative result.

## Feed acceptance and platform boundary

```sh
./scripts/smoke.sh
```

This task starts its own server, verifies gRPC and redundant multicast and
stops only the server PID it started. On Linux x86-64 with .NET 10, both feed
paths pass. macOS ARM64 core tests pass, but Grpc.Core does not supply the
required native server library there; this is not a macOS feed-support claim.

Measured 11 November 2026: 392/392 core tests pass on macOS ARM64 and Linux
x86-64. The Linux run was inside an amd64 container on an ARM64 host; no native
x86 hardware throughput claim follows. Build, generated-document checks and
both feed smoke paths pass. CI now checks TRX counters so an exit-zero test
command with empty discovery cannot count as acceptance.

Project code and original documentation use the existing Apache-2.0 license.
Market samples and third-party dependencies retain separate terms; see
[LICENSING.md](../LICENSING.md). No old benchmark measurement is replaced by
these acceptance runs.
