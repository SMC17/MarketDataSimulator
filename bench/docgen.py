#!/usr/bin/env python3
"""Generate benchmark tables from committed JSON; --check rejects drift."""
import argparse
import difflib
import glob
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
RESULTS = ROOT / "bench/results"
DOCUMENTS = [ROOT / "README.md", ROOT / "BENCHMARKS.md"]

MIN_DELIVERY = 0.99
MAX_OUTBOUND_QUEUE = 100
MAX_UPDATE_QUEUE = 100

REGION = re.compile(
    r"(?P<open><!-- generated: (?P<name>[a-z0-9-]+) -->\n)"
    r"(?P<body>.*?)"
    r"(?P<close>\n?<!-- /generated -->)",
    re.DOTALL)


class MissingResults(Exception):
    """Raised when a region's inputs have not been measured."""


# ------------------------------------------------------------------ loading

def load(name):
    path = RESULTS / name
    if not path.exists():
        raise MissingResults(f"{name} not found in bench/results/")
    with open(path) as handle:
        return json.load(handle)


def load_sweep(tag):
    paths = sorted(glob.glob(str(RESULTS / f"{tag}_s*.json")))
    if not paths:
        raise MissingResults(f"no runs matching {tag}_s*.json")

    rows = []
    for path in paths:
        with open(path) as handle:
            row = json.load(handle)
        if "Error" not in row:
            rows.append(row)
    return sorted(rows, key=lambda r: (r.get("UpdateRatePerInstrument", 0),
                                       r.get("RequestedSubscribers") or r.get("Subscribers", 0)))


def delivery(row):
    """Share of what the server actually published that reached subscribers.

    Measured against the published rate rather than the configured one: the
    generator can undershoot, and charging that to the fan-out would report the
    system as failing when the instrument is what fell short.
    """
    produced = row.get("ServerMeanDisseminatedPerSecond")
    subscribers = row.get("ConnectedSubscribers") or row.get("Subscribers") or 0

    if not produced or not subscribers:
        return None

    return row["MessagesPerSecond"] / (produced * subscribers)


def generator_fidelity(row):
    produced = row.get("ServerMeanDisseminatedPerSecond")
    nominal = row.get("AggregateUpdateRate")
    return produced / nominal if produced and nominal else None


def sustained(row):
    delivered = delivery(row)
    return (row.get("FailedSubscribers", 0) == 0
            and row["ConnectedSubscribers"] == row["RequestedSubscribers"]
            and delivered is not None and delivered >= MIN_DELIVERY
            and (row.get("ServerDroppedUpdates") or 0) == 0
            and (row.get("ServerMaxOutboundQueued") or 0) <= MAX_OUTBOUND_QUEUE
            and (row.get("ServerMaxQueueDepth") or 0) <= MAX_UPDATE_QUEUE)


def multicast_sustained(row):
    """Multicast has no backpressure, so its failure mode is loss, not backlog.

    Judged on the decoder's own sequence-gap counters rather than on the delivery
    ratio, and that is a tightening rather than a loosening. The feed is
    sequenced, so a subscriber that misses a message detects it directly and says
    so; the delivery ratio is a quotient of two rates sampled independently by two
    processes, and carries the noise of both. Where they disagree, a counted gap
    is better evidence than a ratio near a threshold - a run with zero gaps, zero
    missed messages and no stale subscribers lost nothing, whatever the quotient
    rounds to.

    The delivery ratio is still reported in the table, so a reader can see the two
    agree, and a shortfall large enough to matter shows up in both.
    """
    return (row.get("Gaps", 0) == 0
            and row.get("MissedMessages", 0) == 0
            and row.get("Malformed", 0) == 0
            and row.get("StaleSubscribers", 0) == 0
            and (row.get("ServerDroppedUpdates") or 0) == 0)


def environment():
    """The host the measurements were taken on, as recorded when they were taken."""
    data = load("environment.json")

    features = ", ".join(data.get("CpuFeatures") or []) or "—"
    rows = [
        ["CPU", f"{data.get('Cpu', 'unknown')}, {data.get('Cores')} vCPU"],
        ["CPU features", features],
        ["Memory", f"{data.get('MemoryGb')} GB" if data.get("MemoryGb") else "—"],
        ["OS", f"{data.get('Os')}, kernel {data.get('Kernel')}"],
        ["Runtime", f".NET {data.get('DotnetRuntime')}, all projects target `net8.0`"],
        ["Build", "Release, Server GC"],
        ["Topology", "server and load generator as separate processes on the same host"],
    ]
    return table(["", ""], rows)


def assert_one_host_per_generation():
    """Refuses to mix hosts inside one benchmark generation.

    Protocol-v2 artifacts and the archived pre-v2 transport sweep are deliberately
    separate records. Each generation must come from one kernel instance, but the
    archive need not be re-measured merely because the current protocol changed.
    """
    seen = {"v2": {}, "legacy": {}}

    for path in glob.glob(str(RESULTS / "*.json")):
        with open(path) as handle:
            try:
                data = json.load(handle)
            except json.JSONDecodeError:
                continue

        if not isinstance(data, dict):
            continue

        boot = data.get("HostBootId") or data.get("BootId")
        if boot:
            name = Path(path).name
            generation = "v2" if "v2" in name else "legacy"
            seen[generation].setdefault(boot, []).append(name)

    mixed = {generation: boots for generation, boots in seen.items() if len(boots) > 1}
    if mixed:
        lines = ["a benchmark generation contains results from multiple hosts:"]
        for generation, boots in mixed.items():
            lines.append(f"  {generation}:")
            for boot, files in sorted(boots.items(), key=lambda item: -len(item[1])):
                lines.append(f"    {boot}: {len(files)} file(s), e.g. {', '.join(sorted(files)[:3])}")
        lines.append("re-measure that generation on one host rather than publishing a mixture")
        raise MissingResults("\n".join(lines))


def table(header, rows):
    lines = ["| " + " | ".join(header) + " |", "|" + "---|" * len(header)]
    lines += ["| " + " | ".join(row) + " |" for row in rows]
    return "\n".join(lines)


# ----------------------------------------------------------------- regions

def measurement(value):
    """Returns a v2 benchmark measurement's median, accepting scalar legacy values."""
    return value["Median"] if isinstance(value, dict) else value


def v2_environment():
    data = load("protocol-v2.json")
    rows = [
        ["Runtime", f"{data['Runtime']}, Release, {'Server' if data['ServerGc'] else 'Workstation'} GC"],
        ["OS / architecture", f"{data['OperatingSystem']}, {data['Architecture']}"],
        ["Logical processors", f"{data['LogicalProcessors']:,}"],
        ["Monotonic clock", f"{data['StopwatchFrequency'] / 1_000_000:.0f} MHz"],
        ["CRC-32C", f"{data['Crc32CImplementation']} ({'hardware' if data['HardwareAccelerated'] else 'software'})"],
    ]
    return table(["Property", "Recorded value"], rows)


def v2_protocol():
    rows = []
    for row in load("protocol-v2.json")["Results"]:
        rows.append([
            row["Name"], f"{row['PacketBytes']:,} B",
            f"{row['MedianNanoseconds']:.1f} ns",
            f"{row['MinNanoseconds']:.1f}–{row['MaxNanoseconds']:.1f} ns",
            f"{row['MillionOperationsPerSecond']:.2f} M/s",
            f"{row['BytesAllocatedPerOperation']:.0f} B/op",
        ])
    return table(["Case", "Packet", "Median", "Min–max", "Rate", "Allocation"], rows)


def v2_durability():
    data = load("durability-v2.json")
    append = []
    for row in data["Append"]:
        append.append([
            row["Name"], row["Policy"], f"{row['PayloadBytes']:,} B",
            f"{row['MedianNanoseconds']:,.1f} ns",
            f"{row['MinNanoseconds']:,.1f}–{row['MaxNanoseconds']:,.1f} ns",
            f"{row['AppendsPerSecond']:,.0f}/s", f"{row['MedianSyncs']:,}",
            f"{row['BytesAllocatedPerAppend']:.0f} B/op",
        ])

    recovery = data["Recovery"]
    recovery_table = table(
        ["Messages", "Checkpoint", "Full replay", "Checkpoint + tail", "Speed-up"],
        [[f"{recovery['Messages']:,}", f"{recovery['CheckpointSequence']:,}",
          f"{recovery['FullMedianMilliseconds']:.2f} ms "
          f"({recovery['FullMinMilliseconds']:.2f}–{recovery['FullMaxMilliseconds']:.2f})",
          f"{recovery['CheckpointMedianMilliseconds']:.2f} ms "
          f"({recovery['CheckpointMinMilliseconds']:.2f}–"
          f"{recovery['CheckpointMaxMilliseconds']:.2f})",
          f"{recovery['SpeedUp']:.2f}×"]])

    ranges = []
    for row in data["RangeReads"]:
        ranges.append([
            row["Name"], f"{row['Queries']:,}", f"{row['IndexEntries']:,}",
            f"{row['MedianNanosecondsPerRequest'] / 1000:,.1f} µs",
            f"{row['MinNanosecondsPerRequest'] / 1000:,.1f}–"
            f"{row['MaxNanosecondsPerRequest'] / 1000:,.1f} µs",
            f"{row['BytesAllocatedPerRequest']:,.0f} B/request",
        ])

    return (table(["Append contract", "Policy", "Payload", "Median", "Min–max",
                   "Rate", "Syncs/trial", "Allocation"], append)
            + "\n\n" + recovery_table
            + "\n\n" + table(["10-message range", "Queries", "Index entries", "Median",
                                "Min–max", "Allocation"], ranges))


def v2_matching():
    rows = []
    for row in load("matching-v2.json")["Results"]:
        values = []
        for key in ("AddCancelCycleNs", "CancelAddCycleNs", "MatchReplenishCycleNs"):
            value = row[key]
            values.append(f"{measurement(value):.1f} ns")
        rows.append([f"{row['RestingOrders']:,}", *values])
    return table(["Resting orders", "Add + cancel", "Cancel + add", "Match + replenish"], rows)


def v2_books():
    rows = []
    for row in load("books-v2.json")["Results"]:
        rows.append([
            f"{row['Depth']:,}", row["Implementation"],
            f"{measurement(row['MixedNsPerOp']):.1f}",
            f"{measurement(row['TouchNsPerOp']):.1f}",
            f"{measurement(row['SnapshotNsPerOp']):.1f}",
            f"{measurement(row['ClearNsPerOp']):.1f}",
            f"{row['SnapshotBytesPerOp']:.0f}",
        ])
    return table(["Depth", "Implementation", "Mixed ns/op", "Touch ns/op",
                  "Top-10 ns/op", "Clear ns/op", "Publish B/op"], rows)


def v2_queue():
    rows = []
    for row in load("queue-v2.json")["Results"]:
        rows.append([
            row["Name"], f"{row['MedianNanosecondsPerItem']:.1f} ns/item",
            f"{row['MinNanosecondsPerItem']:.1f}–{row['MaxNanosecondsPerItem']:.1f}",
            f"{1000 / row['MedianNanosecondsPerItem']:.1f} M item/s",
            f"{row['BytesPerItem']:.3f} B/item",
        ])
    return table(["Queue", "Median", "Min–max", "Throughput", "Allocation"], rows)


def v2_replay():
    rows = []
    paths = sorted(glob.glob(str(RESULTS / "replay-sample-v2-*.json")))
    if not paths:
        raise MissingResults("no replay-sample-v2-*.json in bench/results/")
    for path in paths:
        data = json.load(open(path))
        for entry in data["Transitions"]:
            rows.append([
                data["Symbol"], str(data["Levels"]), entry["Implementation"],
                f"{entry['RowsCompared']:,}", f"{entry['RowsMatched']:,}",
                f"{entry['MatchRate'] * 100:.4f}%", f"{entry['MessagesPerSecond']:,.0f}",
            ])
    return table(["Symbol", "Depth", "Book", "Transitions", "Exact", "Accuracy", "Msg/s"], rows)


def v2_multicast():
    rows = []
    for row in load("protocolv2-summary.json"):
        rows.append([
            f"{row['Subscribers']:,}", f"{row['MessagesPerSecond']:,.0f}",
            f"{row['MessagesPerSecondPerSubscriber']:,.0f}", f"{row['MeanMs']:.3f} ms",
            f"{row['P50Ms']:.3f} ms", f"{row['P99Ms']:.3f} ms",
            f"{row['Gaps']} / {row['IntegrityFailures']} / {row['LineDivergences']} / {row['StaleSubscribers']}",
        ])
    return table(["Subscribers", "Delivered msg/s", "Per subscriber", "Mean", "p50", "p99",
                  "Gaps / CRC / divergence / stale"], rows)


def v2_headline():
    protocol = next(r for r in load("protocol-v2.json")["Results"]
                    if r["Name"] == "encode + decode + apply")
    queue_row = next(r for r in load("queue-v2.json")["Results"]
                     if r["Name"].startswith("RingBuffer batched"))
    matching_row = max(load("matching-v2.json")["Results"], key=lambda r: r["RestingOrders"])
    replay_files = sorted(glob.glob(str(RESULTS / "replay-sample-v2-*.json")))
    transitions = 0
    exact = True
    for path in replay_files:
        data = json.load(open(path))
        transitions += data["Transitions"][0]["RowsCompared"]
        exact = exact and all(r["RowsMatched"] == r["RowsCompared"] for r in data["Transitions"])
    multicast = max(load("protocolv2-summary.json"), key=lambda r: r["Subscribers"])
    journal = next(r for r in load("durability-v2.json")["Append"]
                   if r["Name"] == "seal + packet WAL")
    rows = [
        ["Feed encode → apply", f"{protocol['MedianNanoseconds']:.1f} ns median",
         f"{protocol['BytesAllocatedPerOperation']:.0f} B/op"],
        ["Batched SPSC hand-off", f"{queue_row['MedianNanosecondsPerItem']:.1f} ns/item median",
         f"{queue_row['BytesPerItem']:.3f} B/item including harness setup"],
        [f"Matching at {matching_row['RestingOrders']:,} resting orders",
         f"{measurement(matching_row['MatchReplenishCycleNs']):.1f} ns/cycle median", "state preserving"],
        ["Committed NASDAQ samples", f"{transitions:,} transitions per implementation",
         "exact" if exact else "mismatch present"],
        ["Seal + journal feed packet", f"{journal['MedianNanoseconds']:,.1f} ns median",
         f"{journal['BytesAllocatedPerAppend']:.0f} B/op; OS-buffered acknowledgement"],
        [f"Loopback multicast, {multicast['Subscribers']:,} subscribers",
         f"{multicast['MessagesPerSecond']:,.0f} delivered msg/s",
         f"{multicast['Gaps']} gaps; {multicast['IntegrityFailures']} CRC failures"],
    ]
    return table(["Path", "Recorded result", "Contract"], rows)

def books_full():
    by_depth = {}
    for row in load("books.json"):
        by_depth.setdefault(row["Depth"], {})[row["Implementation"]] = row

    order = ["SortedArrayBook", "VectorizedBook", "LadderBook", "TreeBook"]
    rows = []
    for depth in sorted(by_depth):
        for name in order:
            row = by_depth[depth].get(name)
            if row:
                rows.append([f"{depth:,}", name,
                             f"{row['MixedNsPerOp']:.1f}", f"{row['TouchNsPerOp']:.1f}",
                             f"{row['SnapshotNsPerOp']:.1f}", f"{row['ClearNsPerOp']:,.0f}",
                             f"{row['SnapshotBytesPerOp']:.0f}"])
    return table(["Depth", "Implementation", "Mixed ns/op", "Touch ns/op",
                  "Top-10 publish ns/op", "Clear ns/op", "Bytes per publish"], rows)


def books_summary():
    by_depth = {}
    for row in load("books.json"):
        by_depth.setdefault(row["Depth"], {})[row["Implementation"]] = row

    order = ["SortedArrayBook", "VectorizedBook", "LadderBook", "TreeBook"]
    rows = []
    for depth in sorted(by_depth):
        d = by_depth[depth]
        if not all(name in d for name in order):
            continue
        rows.append([
            f"{depth:,}",
            " / ".join(f"{d[n]['MixedNsPerOp']:.1f}" for n in order),
            " / ".join(f"{d[n]['SnapshotNsPerOp']:.1f}" for n in order),
            " / ".join(f"{d[n]['SnapshotBytesPerOp']:.0f}" for n in order),
        ])
    return table(["Depth", "Mixed ns/op (array / simd / ladder / tree)",
                  "Top-10 publish ns/op", "Bytes per publish"], rows)


def matching():
    rows = [[f"{row['RestingOrders']:,}", f"{row['AddNsPerOp']:.1f} ns",
             f"{row['CancelNsPerOp']:.1f} ns", f"{row['MatchNsPerOp']:.1f} ns",
             f"{row['MixedNsPerOp']:.1f} ns", f"{row['MixedBytesPerOp']:.1f}"]
            for row in load("matching.json")]
    return table(["Resting orders", "Add", "Cancel", "Match", "Mixed (60/35/5)",
                  "Bytes/op"], rows)


def queue():
    rows = [[row["Name"], f"{row['NanosecondsPerItem']:.1f}",
             f"{1000.0 / row['NanosecondsPerItem']:.1f}", f"{row['BytesPerItem']:.0f}"]
            for row in load("queue.json")]
    return table(["Queue", "ns/item", "M items/s", "Bytes/item"], rows)


def queue_arithmetic():
    """What replacing the channel with the ring is worth on the actual path.

    Generated rather than written down because it is a derived claim, and derived
    claims are the ones that quietly stop matching their inputs.
    """
    rows = {row["Name"]: row for row in load("queue.json")}

    ring = rows.get("RingBuffer (producer + consumer)")
    channel = rows.get("Channel (producer + consumer)")

    if not ring or not channel:
        raise MissingResults("queue.json lacks the concurrent hand-off rows")

    sweep = [r for r in load_sweep("scale50") if sustained(r)]
    if not sweep:
        raise MissingResults("no sustained unicast run to size the update rate from")

    # The hand-off sits upstream of fan-out, so it runs at the update rate, not the
    # message rate. Take the operating point the queue A/B was actually run at.
    reference = min(sweep, key=lambda r: abs(r["RequestedSubscribers"] - 500))
    updates = reference.get("ServerMeanDisseminatedPerSecond") or 0
    subscribers = reference["ConnectedSubscribers"]

    saved_ns = channel["NanosecondsPerItem"] - ring["NanosecondsPerItem"]
    saved_per_second_us = saved_ns * updates / 1000.0
    core_share = saved_per_second_us / 1e6 * 100

    return (
        f"| | |\n|---|---|\n"
        f"| Concurrent hand-off, channel | {channel['NanosecondsPerItem']:.1f} ns/item |\n"
        f"| Concurrent hand-off, ring | **{ring['NanosecondsPerItem']:.1f} ns/item** |\n"
        f"| Ring speed-up in isolation | "
        f"**{channel['NanosecondsPerItem'] / ring['NanosecondsPerItem']:.1f}×** |\n"
        f"| Rate this queue actually carries | {updates:,.0f} updates/s "
        f"(not the {updates * subscribers:,.0f} msg/s of fan-out) |\n"
        f"| Time saved per second of running | "
        f"**{saved_per_second_us:.1f} µs**, or **{core_share:.4f}% of one core** |")


def replay():
    rows = []
    for path in sorted(glob.glob(str(RESULTS / "replay-*.json"))):
        data = json.load(open(path))
        for entry in data["Transitions"]:
            rows.append([data["Symbol"], str(data["Levels"]), entry["Implementation"],
                         f"{entry['RowsCompared']:,}", f"{entry['RowsMatched']:,}",
                         f"{entry['MatchRate'] * 100:.4f}%",
                         f"{entry['MessagesPerSecond']:,.0f}"])
    if not rows:
        raise MissingResults("no replay-*.json in bench/results/")
    return table(["Symbol", "Levels", "Book", "Transitions", "Exact", "Accuracy",
                  "Msg/s"], rows)


def microstructure():
    rows = []
    for session in load("microstructure.json"):
        for bucket in session["Buckets"]:
            rows.append([session["Symbol"], f"{bucket['BucketEvents']}", f"{bucket['Samples']:,}",
                         f"{bucket['ContemporaneousRSquared'] * 100:.2f}%",
                         f"{bucket['ContemporaneousT']:.1f}",
                         f"{bucket['PredictiveRSquared'] * 100:.2f}%",
                         f"{bucket['PredictiveT']:.1f}"])
    return table(["Symbol", "Bucket", "Samples", "Contemporaneous R²", "t",
                  "Predictive R²", "t"], rows)


def microstructure_amzn():
    for session in load("microstructure.json"):
        if session["Symbol"] != "AMZN":
            continue
        rows = [[f"{b['BucketEvents']} events", f"{b['ContemporaneousRSquared'] * 100:.2f}%",
                 f"{b['ContemporaneousT']:.1f}", f"{b['PredictiveRSquared'] * 100:.2f}%",
                 f"{b['PredictiveT']:.1f}"] for b in session["Buckets"]]
        return table(["Bucket", "Contemporaneous R²", "t", "Predictive R²", "t"], rows)
    raise MissingResults("AMZN session not in microstructure.json")


def realism():
    rows = [[s["Name"], f"{s['Observations']:,}", f"{s['ExcessKurtosis']:,.2f}",
             f"{s['AbsAutocorrelation1']:.4f}", f"{s['AbsAutocorrelation10']:.4f}",
             f"{s['Beyond3Sigma'] * 100:.3f}%", f"{s['Beyond5Sigma'] * 100:.3f}%"]
            for s in load("realism.json")]
    return table(["Series", "Observations", "Excess kurtosis", "\\|r\\| ac(1)",
                  "\\|r\\| ac(10)", ">3σ", ">5σ"], rows)


def unicast_sweep(tag):
    rows = []
    for row in load_sweep(tag):
        delivered = delivery(row)
        fidelity = generator_fidelity(row)
        rows.append([
            f"{row['RequestedSubscribers']:,}",
            f"{row['MessagesPerSecond']:,.0f}",
            f"**{row['MeanMs']:.2f}**",
            f"{row['P50Ms']:.2f}", f"{row['P99Ms']:.1f}",
            f"{row['P999Ms']:.1f}", f"{row['MaxMs']:.1f}",
            f"{delivered * 100:.1f}%" if delivered is not None else "—",
            f"{fidelity * 100:.0f}%" if fidelity is not None else "—",
            f"{row.get('ServerCpuPercent')}%", f"{row.get('HostCpuPercent')}%",
            "yes" if sustained(row) else "**NO**",
        ])
    return table(["Subscribers", "Fan-out (msg/s)", "Mean (ms)", "p50", "p99", "p99.9",
                  "Max", "Delivered", "Gen. rate", "Server CPU", "Host CPU",
                  "Sustained"], rows)


def multicast_sweep():
    rows = []
    for row in load_sweep("mcast"):
        delivered = delivery(row)
        rows.append([
            f"{row['Subscribers']:,}",
            f"{row['MessagesPerSecond']:,.0f}",
            f"**{row['MeanMs']:.2f}**",
            f"{row['P50Ms']:.2f}", f"{row['P99Ms']:.1f}", f"{row['MaxMs']:.1f}",
            f"{delivered * 100:.1f}%" if delivered is not None else "—",
            f"{row.get('ServerPacketsPerSecond')}",
            str(row.get("Gaps", 0)), str(row.get("StaleSubscribers", 0)),
            f"{row.get('ServerCpuPercent')}%", f"{row.get('HostCpuPercent')}%",
            "yes" if multicast_sustained(row) else "**NO**",
        ])
    return table(["Subscribers", "Fan-out (msg/s)", "Mean (ms)", "p50", "p99", "Max",
                  "Delivered", "Server pkt/s", "Gaps", "Stale", "Server CPU",
                  "Host CPU", "Sustained"], rows)


def head_to_head():
    """Matched points only.

    Restricted to subscriber counts where *both* transports were actually run.
    Filling the gaps with a dash invites the reader to treat an unmeasured point
    as a failed one, which flatters multicast for free - and multicast does not
    need the help.
    """
    unicast = {r["RequestedSubscribers"]: r for r in load_sweep("scale50")}
    rows = []

    for row in load_sweep("mcast"):
        subscribers = row["Subscribers"]
        peer = unicast.get(subscribers)

        if peer is None:
            continue

        multicast_mean = (f"**{row['MeanMs']:.2f} ms**" if multicast_sustained(row)
                          else f"{row['MeanMs']:.2f} ms (not sustained)")

        if sustained(peer) and multicast_sustained(row):
            improvement = f"**{peer['MeanMs'] / row['MeanMs']:.1f}×**"
        else:
            improvement = "—"

        unicast_mean = (f"{peer['MeanMs']:.2f} ms" if sustained(peer)
                        else f"{peer['MeanMs']:.2f} ms (not sustained)")

        rows.append([f"{subscribers:,}", unicast_mean, multicast_mean, improvement])

    if not rows:
        raise MissingResults("no subscriber count was measured on both transports")

    return table(["Subscribers", "Unicast mean", "Multicast mean", "Improvement"], rows)


def equal_work():
    """The O(N) claim, stated as the comparison it actually rests on.

    Two runs matched on messages per second and differing only in how many
    subscribers that work is spread across. Equal work, different audience: if
    latency were a function of load it would be the same in both rows.
    """
    candidates = load_sweep("scale50") + load_sweep("scale5")
    sustainable = [r for r in candidates if sustained(r)]

    if len(sustainable) < 2:
        raise MissingResults("need sustained runs from both scale50 and scale5")

    # Pick the closest pair on delivered rate that differ most in audience size.
    best = None
    for left in sustainable:
        for right in sustainable:
            if right["RequestedSubscribers"] <= left["RequestedSubscribers"]:
                continue
            gap = abs(left["MessagesPerSecond"] - right["MessagesPerSecond"])
            tolerance = 0.1 * max(left["MessagesPerSecond"], right["MessagesPerSecond"])
            if gap > tolerance:
                continue
            ratio = right["RequestedSubscribers"] / left["RequestedSubscribers"]
            if best is None or ratio > best[0]:
                best = (ratio, left, right)

    if best is None:
        raise MissingResults("no two sustained runs share a fan-out rate")

    _, left, right = best
    rows = []
    for row in (left, right):
        rows.append([f"{row['RequestedSubscribers']:,}",
                     f"{row.get('AggregateUpdateRate', 0):g} upd/s",
                     f"{row['MessagesPerSecond']:,.0f} msg/s",
                     f"**{row['MeanMs']:.2f} ms**"])
    return table(["Subscribers", "Feed rate", "Fan-out", "Mean latency"], rows)


def cost_per_message():
    """Server CPU per delivered message at each transport's sustained ceiling.

    The comparison the whole multicast argument reduces to: fan-out under unicast
    is work the server does per subscriber, and under multicast it is work the
    server does not do at all.
    """
    unicast = [r for r in load_sweep("scale50") if sustained(r)]
    mcast = [r for r in load_sweep("mcast") if multicast_sustained(r)]

    if not unicast or not mcast:
        raise MissingResults("need a sustained run from each transport")

    best_unicast = max(unicast, key=lambda r: r["RequestedSubscribers"])
    best_mcast = max(mcast, key=lambda r: r["Subscribers"])

    def micros(row):
        cpu = row.get("ServerCpuPercent")
        return (cpu / 100.0) * 1e6 / row["MessagesPerSecond"] if cpu else None

    unicast_cost = micros(best_unicast)
    mcast_cost = micros(best_mcast)

    rows = [
        ["Unicast gRPC", f"{best_unicast['RequestedSubscribers']:,}",
         f"{best_unicast['MessagesPerSecond']:,.0f}",
         f"{best_unicast.get('ServerCpuPercent')}%",
         f"**{unicast_cost:.2f} µs**" if unicast_cost else "—"],
        ["Multicast", f"{best_mcast['Subscribers']:,}",
         f"{best_mcast['MessagesPerSecond']:,.0f}",
         f"{best_mcast.get('ServerCpuPercent')}%",
         f"**{mcast_cost:.2f} µs**" if mcast_cost else "—"],
    ]

    return table(["Transport", "Highest sustained subscribers", "Messages/s", "Server CPU",
                  "Server CPU per message"], rows)


def headline():
    """The two-row summary the documents open with."""
    unicast = [r for r in load_sweep("scale50") if sustained(r)]
    mcast = [r for r in load_sweep("mcast") if multicast_sustained(r)]

    if not unicast or not mcast:
        raise MissingResults("need a sustained run from each transport")

    best_unicast = max(unicast, key=lambda r: r["RequestedSubscribers"])
    best_mcast = max(mcast, key=lambda r: r["Subscribers"])

    # Whether a run above the best sustained one was measured and failed. Without
    # that, the largest sustained point is the top of the sweep, not a ceiling, and
    # calling it one claims a limit that was never found.
    unicast_failed = [r for r in load_sweep("scale50")
                      if not sustained(r)
                      and r["RequestedSubscribers"] > best_unicast["RequestedSubscribers"]]
    mcast_failed = [r for r in load_sweep("mcast")
                    if not multicast_sustained(r)
                    and r["Subscribers"] > best_mcast["Subscribers"]]

    def qualify(failed):
        return "ceiling (next point up failed)" if failed else "top of sweep, not a limit"

    rows = [
        ["Unicast gRPC", f"{best_unicast['RequestedSubscribers']:,}",
         qualify(unicast_failed),
         f"{best_unicast['MessagesPerSecond']:,.0f} msg/s",
         f"{best_unicast['MeanMs']:.2f} ms", f"{best_unicast['P99Ms']:.1f} ms",
         f"{best_unicast.get('ServerCpuPercent')}%"],
        ["**Multicast**", f"**{best_mcast['Subscribers']:,}**",
         qualify(mcast_failed),
         f"**{best_mcast['MessagesPerSecond']:,.0f} msg/s**",
         f"**{best_mcast['MeanMs']:.2f} ms**", f"**{best_mcast['P99Ms']:.1f} ms**",
         f"**{best_mcast.get('ServerCpuPercent')}%**"],
    ]
    return table(["Transport", "Highest sustained subscribers", "Is that a ceiling?",
                  "Fan-out at that point", "Mean latency", "p99", "Server CPU"], rows)


def batching():
    rows = []
    for batch in (1, 4, 16, 64):
        found = load_sweep(f"batch{batch}")
        row = found[0]
        rows.append([str(batch), f"{row['MessagesPerSecond']:,.0f}",
                     f"**{row['MeanMs']:.2f}**", f"{row['P99Ms']:.1f}",
                     f"{row.get('ServerPacketsPerSecond')}",
                     f"{row.get('ServerCpuPercent')}%", f"{row.get('HostCpuPercent')}%"])
    return table(["Max batch", "Fan-out (msg/s)", "Mean (ms)", "p99", "Server pkt/s",
                  "Server CPU", "Host CPU"], rows)


def queue_ab():
    rows = []
    for kind, label in (("qchannel", "Channel"), ("qring", "Ring buffer")):
        means = []
        for i in (1, 2, 3):
            means.append(load_sweep(f"{kind}{i}")[0]["MeanMs"])
        rows.append([label, ", ".join(f"{m:.2f}" for m in means),
                     f"{sum(means) / len(means):.2f}",
                     f"{max(means) - min(means):.2f}"])
    return table(["Dissemination queue", "Run means (ms)", "Mean of means",
                  "Spread"], rows)


def repeatability():
    rows = []
    for tag, point in (("rep", "4,000 subscribers, 10 upd/s"),
                       ("qchannel", "500 subscribers, 100 upd/s")):
        means = [load_sweep(f"{tag}{i}")[0]["MeanMs"] for i in (1, 2, 3)]
        rows.append([point, str(len(means)),
                     f"{sorted(means)[len(means) // 2]:.2f} ms",
                     f"{min(means):.2f}", f"{max(means):.2f}",
                     f"{max(means) / min(means):.2f}×"])
    return table(["Point", "Runs", "Median", "Min", "Max", "Spread"], rows)


REGIONS = {
    "v2-environment": v2_environment,
    "v2-protocol": v2_protocol,
    "v2-durability": v2_durability,
    "v2-matching": v2_matching,
    "v2-books": v2_books,
    "v2-queue": v2_queue,
    "v2-replay": v2_replay,
    "v2-multicast": v2_multicast,
    "v2-headline": v2_headline,
    "books-full": books_full,
    "books-summary": books_summary,
    "matching": matching,
    "queue": queue,
    "replay": replay,
    "microstructure": microstructure,
    "microstructure-amzn": microstructure_amzn,
    "realism": realism,
    "unicast-sweep": lambda: unicast_sweep("scale50"),
    "unicast-sweep-light": lambda: unicast_sweep("scale5"),
    "multicast-sweep": multicast_sweep,
    "head-to-head": head_to_head,
    "environment": environment,
    "equal-work": equal_work,
    "headline": headline,
    "cost-per-message": cost_per_message,
    "batching": batching,
    "queue-ab": queue_ab,
    "queue-arithmetic": queue_arithmetic,
    "repeatability": repeatability,
}


def render(document, strict):
    text = document.read_text()
    problems = []

    def replace(match):
        name = match.group("name")
        if name not in REGIONS:
            problems.append(f"{document.name}: unknown generated region '{name}'")
            return match.group(0)
        try:
            body = REGIONS[name]()
        except MissingResults as missing:
            problems.append(f"{document.name}: region '{name}' has no results ({missing})")
            return match.group(0)
        return match.group("open") + body + match.group("close")

    updated = REGION.sub(replace, text)

    if problems and strict:
        for problem in problems:
            print(problem, file=sys.stderr)

    return text, updated, problems


def main():
    parser = argparse.ArgumentParser()
    group = parser.add_mutually_exclusive_group(required=True)
    group.add_argument("--write", action="store_true", help="rewrite generated regions in place")
    group.add_argument("--check", action="store_true", help="fail if any region is stale")
    arguments = parser.parse_args()

    failed = False

    try:
        assert_one_host_per_generation()
    except MissingResults as mixed:
        print(mixed, file=sys.stderr)
        sys.exit(1)

    for document in DOCUMENTS:
        original, updated, problems = render(document, strict=True)

        if problems:
            failed = True
            continue

        if arguments.write:
            if updated != original:
                document.write_text(updated)
                print(f"updated {document.name}")
            else:
                print(f"{document.name} already current")
            continue

        if updated != original:
            failed = True
            print(f"{document.name} is stale; run: python3 bench/docgen.py --write")
            diff = difflib.unified_diff(original.splitlines(), updated.splitlines(),
                                        fromfile=f"{document.name} (committed)",
                                        tofile=f"{document.name} (from results)",
                                        lineterm="")
            print("\n".join(diff))
        else:
            print(f"{document.name} matches bench/results/")

    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
