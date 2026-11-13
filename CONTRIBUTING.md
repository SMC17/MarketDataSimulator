# Contributing to MarketDataSimulator

Thanks for looking. This repo follows the [SMC17 standards](https://github.com/SMC17/SMC17/blob/main/STANDARDS.md); the short version is below.

## Build and test

Requirements: .NET SDK 10.

```sh
dotnet test MarketDataSimulator.sln
```

A change is ready when that passes from a fresh clone.

## Finding something to do

- Issues labelled [`good first issue`](https://github.com/SMC17/MarketDataSimulator/labels/good%20first%20issue) or [`status: ready`](https://github.com/SMC17/MarketDataSimulator/labels/status%3A%20ready) are scoped and waiting.
- [ROADMAP.md](ROADMAP.md) says what comes next and why.
- Comment on an issue before starting anything large, so work is not duplicated.

## Pull requests

- One topic per PR; link the issue (`Closes #N`).
- Any performance or correctness number needs its command, commit, toolchain and machine. Numbers without them are removed before merge.
- Disclose AI assistance in the PR description.
- Be kind: see [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md). Security issues go through [SECURITY.md](SECURITY.md).
