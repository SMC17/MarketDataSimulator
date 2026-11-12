# Replay-sample provenance

The committed AMZN/MSFT `.csv.gz` pairs are 20,000-row slices described in this
project as LOBSTER reconstructions of NASDAQ events and published depth.
`SHA256SUMS` identifies the exact committed bytes used by the replay tests.

LOBSTER's [official sample page](https://php.lobsterdata.com/info/DataSamples.php)
describes its samples as based on the official NASDAQ Historical TotalView-ITCH
sample. The existing fetch helper uses commit-pinned third-party mirrors for
larger sessions; pinning a mirror is provenance, not permission to relicense it.

These market-data files are **outside this project's Apache-2.0 code grant**.
No additional data redistribution rights are granted here. Consult the actual
upstream terms for your intended data use and distribution. Do not label these
samples as original synthetic data or assume that the code license covers them.

Historical benchmark results remain tied to their original inputs, runtime and
hardware. This licensing clarification does not replace or remeasure them.
