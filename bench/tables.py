#!/usr/bin/env python3
"""
Renders every table and inline figure the documentation quotes, directly from the
JSON in bench/results/.

The reason this exists: an audit of the docs found a benchmark figure that had
drifted from its results file by a factor of six, and drifted in the direction
that strengthened the argument the surrounding prose was making. That is the
worst possible failure mode for a document whose whole claim is that its numbers
are real. Hand-transcription is the defect; removing the hand is the fix.

Anything the docs assert should be printed here, so `make docs-check` style
verification is a diff rather than a reading exercise.

Usage:
    python3 bench/tables.py                # every section
    python3 bench/tables.py books matching # named sections only
"""
import glob
import json
import os
import platform
import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
RESULTS = ROOT / "bench/results"

# A run only counts as sustained if the subscribers all stayed connected, saw the
# whole feed, and the server never fell behind enough to drop or build a backlog.
MIN_DELIVERY = 0.99
MAX_OUTBOUND_QUEUE = 100
MAX_UPDATE_QUEUE = 100


MISSING = []


def load(name):
    """Loads a results file, recording - loudly - when one is absent.

    A section that silently renders nothing when its input is missing is the same
    class of defect this script exists to prevent: the reader cannot tell an
    empty section from an unmeasured one.
    """
    path = RESULTS / name
    if not path.exists():
        MISSING.append(name)
        return None
    with open(path) as handle:
        return json.load(handle)


def load_sweep(tag):
    rows = []
    paths = sorted(glob.glob(str(RESULTS / f"{tag}_s*.json")))

    if not paths:
        MISSING.append(f"{tag}_s*.json")

    for path in paths:
        with open(path) as handle:
            row = json.load(handle)
        if "Error" not in row:
            rows.append(row)
    return sorted(rows, key=lambda r: (r.get("UpdateRatePerInstrument", 0), r["RequestedSubscribers"]))


def delivery(row):
    """Share of the updates the server actually produced that reached subscribers.

    Deliberately measured against what the engine published, not against the rate
    the harness was asked for. Those differ: on a slower host the update generator
    undershoots its nominal rate, and dividing by the nominal rate books that
    shortfall as though subscribers had lost data. They are opposite problems -
    one is the fan-out failing, the other is the harness failing - and a single
    ratio that cannot tell them apart is not evidence about either.

    Returns None when the server-side rate was not recorded.
    """
    produced = row.get("ServerMeanDisseminatedPerSecond")
    connected = row.get("ConnectedSubscribers") or 0

    if not produced or not connected:
        return None

    return row["MessagesPerSecond"] / (produced * connected)


def generator_fidelity(row):
    """How close the update generator came to the rate it was configured for."""
    produced = row.get("ServerMeanDisseminatedPerSecond")
    nominal = row.get("AggregateUpdateRate")

    if not produced or not nominal:
        return None

    return produced / nominal


def sustained(row):
    delivered = delivery(row)

    return (row.get("FailedSubscribers", 0) == 0
            and row["ConnectedSubscribers"] == row["RequestedSubscribers"]
            and delivered is not None and delivered >= MIN_DELIVERY
            and (row.get("ServerDroppedUpdates") or 0) == 0
            and (row.get("ServerMaxOutboundQueued") or 0) <= MAX_OUTBOUND_QUEUE
            and (row.get("ServerMaxQueueDepth") or 0) <= MAX_UPDATE_QUEUE)


def heading(text):
    print()
    print("=" * 78)
    print(text)
    print("=" * 78)


def rows_to_table(header, rows):
    print("| " + " | ".join(header) + " |")
    print("|" + "---|" * len(header))
    for row in rows:
        print("| " + " | ".join(row) + " |")


# --------------------------------------------------------------------- sections

def section_environment():
    heading("Environment (BENCHMARKS.md > Environment)")

    cpu = "unknown"
    try:
        with open("/proc/cpuinfo") as handle:
            for line in handle:
                if line.startswith("model name"):
                    cpu = line.split(":", 1)[1].strip()
                    break
    except OSError:
        pass

    memory = "unknown"
    try:
        with open("/proc/meminfo") as handle:
            total_kb = int(handle.readline().split()[1])
        memory = f"{total_kb / 1024 / 1024:.0f} GB"
    except (OSError, ValueError):
        pass

    try:
        runtime = subprocess.run(["dotnet", "--version"], capture_output=True, text=True,
                                 timeout=60).stdout.strip()
    except (OSError, subprocess.SubprocessError):
        runtime = "unknown"

    print(f"| CPU | {cpu}, {os.cpu_count()} vCPU |")
    print(f"| Memory | {memory} |")
    print(f"| OS | {platform.system()} {platform.release()} |")
    print(f"| SDK | .NET {runtime} |")


def section_books():
    data = load("books.json")
    if not data:
        return

    heading("Order books (README + BENCHMARKS.md > Order book micro-benchmark)")

    results = data["Results"] if isinstance(data, dict) else data
    by_depth = {}
    for row in results:
        by_depth.setdefault(row["Depth"], {})[row["Implementation"]] = row

    order = ["SortedArrayBook", "VectorizedBook", "LadderBook", "TreeBook"]

    print("Full table:")
    rows = []
    for depth in sorted(by_depth):
        for name in order:
            row = by_depth[depth].get(name)
            if not row:
                continue
            rows.append([f"{depth:,}", name.replace("Book", ""),
                         f"{row['MixedNsPerOp']:.1f}", f"{row['TouchNsPerOp']:.1f}",
                         f"{row['SnapshotNsPerOp']:.1f}", f"{row['ClearNsPerOp']:,.0f}",
                         f"{row['SnapshotBytesPerOp']:.0f}"])
    rows_to_table(["Depth", "Book", "Mixed ns", "Touch ns", "Top-10 ns", "Clear ns", "B/publish"], rows)

    print()
    print("README summary (mixed / top-10 publish, array vs ladder vs tree vs simd):")
    rows = []
    for depth in sorted(by_depth):
        d = by_depth[depth]
        if not all(name in d for name in order):
            continue
        mixed = " / ".join(f"{d[name]['MixedNsPerOp']:.1f}" for name in order)
        top10 = " / ".join(f"{d[name]['SnapshotNsPerOp']:.1f}" for name in order)
        alloc = " / ".join(f"{d[name]['SnapshotBytesPerOp']:.0f}" for name in order)
        rows.append([f"{depth:,}", mixed, top10, alloc])
    rows_to_table(["Depth", "Mixed ns/op (array/simd/ladder/tree)",
                   "Top-10 publish ns/op", "Bytes per publish"], rows)

    # Claims the prose makes about ratios, computed rather than remembered.
    print()
    for depth in sorted(by_depth):
        d = by_depth[depth]
        if "SortedArrayBook" not in d or "TreeBook" not in d:
            continue
        array, tree, ladder = d["SortedArrayBook"], d["TreeBook"], d.get("LadderBook")
        print(f"  depth {depth:>5}: publish array {array['SnapshotNsPerOp']:.1f} ns vs "
              f"tree {tree['SnapshotNsPerOp']:.1f} ns ({tree['SnapshotNsPerOp'] / array['SnapshotNsPerOp']:.1f}x)"
              + (f", ladder {ladder['SnapshotNsPerOp']:.1f} ns "
                 f"({ladder['SnapshotNsPerOp'] / array['SnapshotNsPerOp']:.1f}x)" if ladder else ""))


def section_matching():
    data = load("matching.json")
    if not data:
        return

    heading("Matching engine (README + BENCHMARKS.md > Matching engine micro-benchmark)")

    results = data["Results"] if isinstance(data, dict) else data
    rows = []
    for row in results:
        rows.append([f"{row['RestingOrders']:,}",
                     f"{row['AddNsPerOp']:.1f} ns", f"{row['CancelNsPerOp']:.1f} ns",
                     f"{row['MatchNsPerOp']:.1f} ns", f"{row['MixedNsPerOp']:.1f} ns",
                     f"{row['MixedBytesPerOp']:.1f}"])
    rows_to_table(["Resting orders", "Add", "Cancel", "Match", "Mixed (60/35/5)", "B/op"], rows)


def section_queue():
    data = load("queue.json")
    if not data:
        return

    heading("Queue hand-off (BENCHMARKS.md > A lock-free queue)")

    results = data["Results"] if isinstance(data, dict) else data
    rows = [[row["Name"], f"{row['NanosecondsPerItem']:.1f}",
             f"{1000.0 / row['NanosecondsPerItem']:.2f}",
             f"{row['BytesPerItem']:.2f}"] for row in results]
    rows_to_table(["Queue", "ns/item", "M items/s", "B/item"], rows)


def section_replay():
    heading("Real-data replay (README + BENCHMARKS.md > Validation against real NASDAQ data)")

    rows = []
    parser_rates = []
    for path in sorted(glob.glob(str(RESULTS / "replay-*.json"))):
        data = json.load(open(path))
        for entry in data["Transitions"]:
            rows.append([data["Symbol"], str(data["Levels"]), entry["Implementation"],
                         f"{entry['RowsCompared']:,}", f"{entry['RowsMatched']:,}",
                         f"{entry['MatchRate'] * 100:.4f} %",
                         f"{entry['MessagesPerSecond']:,.0f}"])
        parser = data.get("Parser")
        if parser:
            parser_rates.append((data["Symbol"], parser))

    rows_to_table(["Symbol", "Levels", "Book", "Transitions", "Exact", "Accuracy", "Msg/s"], rows)

    if parser_rates:
        print()
        print("Parser (zero-allocation CSV reader):")
        for symbol, parser in parser_rates:
            print(f"  {symbol}: {parser['MessagesPerSecond']:,.0f} msg/s, "
                  f"{parser['MebibytesPerSecond']:.1f} MiB/s, "
                  f"{parser['BytesPerMessage']} B/msg")
        lo = min(p["MessagesPerSecond"] for _, p in parser_rates)
        hi = max(p["MessagesPerSecond"] for _, p in parser_rates)
        mlo = min(p["MebibytesPerSecond"] for _, p in parser_rates)
        mhi = max(p["MebibytesPerSecond"] for _, p in parser_rates)
        print(f"  range: {lo / 1e6:.1f}-{hi / 1e6:.1f}M msg/s, {mlo:.0f}-{mhi:.0f} MiB/s")


def section_microstructure():
    data = load("microstructure.json")
    if not data:
        return

    heading("Order flow imbalance (README + BENCHMARKS.md > Does the book predict anything?)")

    sessions = data["Sessions"] if isinstance(data, dict) else data
    for session in sessions:
        print()
        print(f"{session['Symbol']} ({session['Session']})")
        rows = []
        best_predictive = None
        for bucket in session["Buckets"]:
            rows.append([f"{bucket['Events']}", f"{bucket['Samples']:,}",
                         f"{bucket['ContemporaneousRSquared'] * 100:.2f}%",
                         f"{bucket['ContemporaneousT']:.1f}",
                         f"{bucket['PredictiveRSquared'] * 100:.2f}%",
                         f"{bucket['PredictiveT']:.1f}"])
            if best_predictive is None or bucket["PredictiveRSquared"] > best_predictive["PredictiveRSquared"]:
                best_predictive = bucket
        rows_to_table(["Bucket", "Samples", "Contemp R2", "t", "Predictive R2", "t"], rows)
        if best_predictive:
            print(f"  peak predictive R2: {best_predictive['PredictiveRSquared'] * 100:.2f}% "
                  f"at {best_predictive['Events']} events (t = {best_predictive['PredictiveT']:.1f})")


def section_realism():
    data = load("realism.json")
    if not data:
        return

    heading("Stylized facts (BENCHMARKS.md > realism)")

    series = data["Series"] if isinstance(data, dict) else data
    rows = [[s["Name"], f"{s['Observations']:,}", f"{s['ExcessKurtosis']:,.2f}",
             f"{s['AbsoluteAutocorrelation1']:.4f}", f"{s['AbsoluteAutocorrelation10']:.4f}",
             f"{s['ReturnAutocorrelation1']:.4f}",
             f"{s['BeyondThreeSigma'] * 100:.3f}%", f"{s['BeyondFiveSigma'] * 100:.3f}%"]
            for s in series]
    rows_to_table(["Series", "Obs", "Excess kurtosis", "|r| ac(1)", "|r| ac(10)",
                   "r ac(1)", ">3 sigma", ">5 sigma"], rows)


def sweep_table(tag, title):
    rows = load_sweep(tag)
    if not rows:
        return None

    heading(title)
    table = []
    for row in rows:
        delivered = delivery(row)
        fidelity = generator_fidelity(row)
        table.append([
            f"{row['RequestedSubscribers']:,}",
            f"{row.get('AggregateUpdateRate', 0):g}",
            f"{row['MessagesPerSecond']:,.0f}",
            f"**{row['MeanMs']:.2f}**",
            f"{row['P50Ms']:.2f}",
            f"{row['P99Ms']:.1f}",
            f"{row['P999Ms']:.1f}",
            f"{row['MaxMs']:.1f}",
            f"{delivered * 100:.1f}%" if delivered is not None else "-",
            f"{fidelity * 100:.0f}%" if fidelity is not None else "-",
            f"{row.get('ServerCpuPercent')}%",
            f"{row.get('HostCpuPercent')}%",
            "yes" if sustained(row) else "**NO**",
        ])
    rows_to_table(["Subscribers", "Feed (upd/s)", "Fan-out (msg/s)", "Mean (ms)", "p50",
                   "p99", "p99.9", "Max", "Delivered", "Gen. rate", "Server CPU", "Host CPU",
                   "Sustained"], table)

    ok = [r for r in rows if sustained(r)]
    if ok:
        best = max(ok, key=lambda r: r["RequestedSubscribers"])
        print()
        print(f"  max sustained: {best['RequestedSubscribers']:,} subscribers, "
              f"{best['MessagesPerSecond']:,.0f} msg/s, {best['MeanMs']:.2f} ms mean, "
              f"server CPU {best.get('ServerCpuPercent')}% of {best.get('HostCores', '?')}00%")
    return rows


def multicast_sustained(row):
    """Multicast has no backpressure, so its failure modes are loss, not backlog."""
    return (row.get("Gaps", 0) == 0
            and row.get("MissedMessages", 0) == 0
            and row.get("Malformed", 0) == 0
            and row.get("StaleSubscribers", 0) == 0
            and (row.get("ServerDroppedUpdates") or 0) == 0
            and (multicast_delivery(row) or 0) >= MIN_DELIVERY)


def multicast_delivery(row):
    produced = row.get("ServerMeanDisseminatedPerSecond")
    subscribers = row.get("Subscribers") or 0

    if not produced or not subscribers:
        return None

    return row["MessagesPerSecond"] / (produced * subscribers)


def multicast_table(tag, title):
    rows = load_sweep(tag)
    if not rows:
        return None

    heading(title)
    table = []
    for row in rows:
        delivered = multicast_delivery(row)
        table.append([
            f"{row['Subscribers']:,}",
            f"{row['MessagesPerSecond']:,.0f}",
            f"**{row['MeanMs']:.2f}**",
            f"{row['P50Ms']:.2f}",
            f"{row['P99Ms']:.1f}",
            f"{row['MaxMs']:.1f}",
            f"{delivered * 100:.1f}%" if delivered is not None else "-",
            f"{row.get('ServerPacketsPerSecond')}",
            str(row.get("Gaps", 0)),
            str(row.get("StaleSubscribers", 0)),
            f"{row.get('ServerCpuPercent')}%",
            f"{row.get('HostCpuPercent')}%",
            "yes" if multicast_sustained(row) else "**NO**",
        ])
    rows_to_table(["Subscribers", "Fan-out (msg/s)", "Mean (ms)", "p50", "p99", "Max",
                   "Delivered", "Server pkt/s", "Gaps", "Stale", "Server CPU", "Host CPU",
                   "Sustained"], table)

    ok = [r for r in rows if multicast_sustained(r)]
    if ok:
        best = max(ok, key=lambda r: r["Subscribers"])
        print()
        print(f"  max sustained: {best['Subscribers']:,} subscribers, "
              f"{best['MessagesPerSecond']:,.0f} msg/s, {best['MeanMs']:.2f} ms mean, "
              f"server CPU {best.get('ServerCpuPercent')}%")
    print()
    packets = [r.get("ServerPacketsPerSecond") for r in rows if r.get("ServerPacketsPerSecond")]
    if packets:
        print(f"  server packets/s across {min(r['Subscribers'] for r in rows):,}-"
              f"{max(r['Subscribers'] for r in rows):,} subscribers: "
              f"{min(packets):.1f} to {max(packets):.1f} "
              f"(the point of the whole exercise: flat in the audience size)")
    return rows


def section_sweeps():
    unicast = sweep_table("scale50", "Unicast sweep, 100 upd/s aggregate (BENCHMARKS.md > Unicast)")
    light = sweep_table("scale5", "Unicast sweep, 10 upd/s aggregate (BENCHMARKS.md > Unicast)")
    mcast = multicast_table("mcast", "Multicast sweep (BENCHMARKS.md > Multicast)")

    if unicast and mcast:
        heading("Head to head (README + BENCHMARKS.md > Head to head)")
        by_subs = {r["RequestedSubscribers"]: r for r in unicast}
        rows = []
        for row in mcast:
            subscribers = row["Subscribers"]
            peer = by_subs.get(subscribers)
            if peer and sustained(peer):
                rows.append([f"{subscribers:,}", f"{peer['MeanMs']:.2f} ms",
                             f"**{row['MeanMs']:.2f} ms**",
                             f"**{peer['MeanMs'] / row['MeanMs']:.1f}x**"])
            elif peer:
                rows.append([f"{subscribers:,}", f"{peer['MeanMs']:.2f} ms (not sustained)",
                             f"**{row['MeanMs']:.2f} ms**", "-"])
            else:
                rows.append([f"{subscribers:,}", "not sustained",
                             f"**{row['MeanMs']:.2f} ms**", "-"])
        rows_to_table(["Subscribers", "Unicast mean", "Multicast mean", "Improvement"], rows)

    if light:
        heading("The O(N) claim: equal work, different audience (README > The short version)")
        # Same messages/second, different subscriber counts - the comparison the
        # linear-latency claim rests on.
        pairs = []
        for row in (unicast or []) + light:
            pairs.append((row["MessagesPerSecond"], row))
        pairs.sort()
        for rate, row in pairs:
            print(f"  {row['RequestedSubscribers']:>6,} subscribers @ "
                  f"{row.get('AggregateUpdateRate', 0):>5g} upd/s -> "
                  f"{rate:>9,.0f} msg/s, mean {row['MeanMs']:>7.2f} ms, "
                  f"{'sustained' if sustained(row) else 'NOT SUSTAINED'}")


def section_batching():
    heading("Multicast batching (BENCHMARKS.md > Batching)")
    rows = []
    for batch in (1, 4, 16, 64):
        found = load_sweep(f"batch{batch}")
        if not found:
            continue
        row = found[0]
        rows.append([str(batch), f"{row['MessagesPerSecond']:,.0f}",
                     f"{row['MeanMs']:.2f}", f"{row['P99Ms']:.1f}",
                     f"{row.get('ServerCpuPercent')}%", f"{row.get('HostCpuPercent')}%"])
    if rows:
        rows_to_table(["Max batch", "Fan-out (msg/s)", "Mean (ms)", "p99", "Server CPU", "Host CPU"], rows)
        if len(rows) > 1:
            first, last = rows[0], rows[-1]
            print()
            print(f"  batch 1 -> {last[0]}: mean {first[2]} -> {last[2]} ms, "
                  f"p99 {first[3]} -> {last[3]} ms, host CPU {first[5]} -> {last[5]}")


def section_queue_ab():
    heading("Channel vs ring on the dissemination path (BENCHMARKS.md > A lock-free queue)")
    for kind in ("qchannel", "qring"):
        means = []
        for i in (1, 2, 3):
            found = load_sweep(f"{kind}{i}")
            if found:
                means.append(found[0]["MeanMs"])
        if means:
            spread = max(means) - min(means)
            print(f"  {kind:>9}: " + ", ".join(f"{m:.2f}" for m in means) +
                  f" ms  (mean {sum(means) / len(means):.2f}, spread {spread:.2f})")
    print()
    print("  Read the spread within each option before believing any gap between them.")


def section_repeatability():
    heading("Repeatability (BENCHMARKS.md > Repeatability)")
    means = []
    for i in (1, 2, 3):
        found = load_sweep(f"rep{i}")
        if found:
            row = found[0]
            means.append(row["MeanMs"])
            print(f"  run {i}: {row['RequestedSubscribers']:,} subscribers, "
                  f"mean {row['MeanMs']:.2f} ms, p99 {row['P99Ms']:.1f} ms, "
                  f"{row['MessagesPerSecond']:,.0f} msg/s")
    if len(means) > 1:
        average = sum(means) / len(means)
        print(f"  mean of means {average:.2f} ms, spread {max(means) - min(means):.2f} ms "
              f"({(max(means) - min(means)) / average * 100:.1f}% of the mean)")


SECTIONS = {
    "environment": section_environment,
    "books": section_books,
    "matching": section_matching,
    "queue": section_queue,
    "replay": section_replay,
    "microstructure": section_microstructure,
    "realism": section_realism,
    "sweeps": section_sweeps,
    "batching": section_batching,
    "queue-ab": section_queue_ab,
    "repeatability": section_repeatability,
}


def main():
    wanted = sys.argv[1:] or list(SECTIONS)
    unknown = [name for name in wanted if name not in SECTIONS]
    if unknown:
        sys.exit(f"unknown section(s): {', '.join(unknown)}\navailable: {', '.join(SECTIONS)}")
    for name in wanted:
        SECTIONS[name]()
    print()

    if MISSING:
        print("MISSING RESULTS - these sections rendered nothing:")
        for name in MISSING:
            print(f"  {name}")
        print()
        print("Regenerate with the commands in BENCHMARKS.md > Reproducing.")
        sys.exit(1)


if __name__ == "__main__":
    main()
