#!/usr/bin/env python3
"""Renders result JSON from bench/run.py as the markdown tables used in BENCHMARKS.md."""
import argparse
import glob
import json
import os
from pathlib import Path

RESULTS = Path(__file__).resolve().parent / "results"

# A run only counts as sustained if the subscribers all stayed connected, saw the
# whole feed, and the server never fell behind enough to drop or to build a backlog.
MIN_DELIVERY = 0.99
MAX_OUTBOUND_QUEUE = 100
MAX_UPDATE_QUEUE = 100


def sustained(row):
    return (row.get("FailedSubscribers", 0) == 0
            and row["ConnectedSubscribers"] == row["RequestedSubscribers"]
            and (row.get("DeliveryRatio") or 0) >= MIN_DELIVERY
            and (row.get("ServerDroppedUpdates") or 0) == 0
            and (row.get("ServerMaxOutboundQueued") or 0) <= MAX_OUTBOUND_QUEUE
            and (row.get("ServerMaxQueueDepth") or 0) <= MAX_UPDATE_QUEUE)


def load(tag):
    rows = []
    for path in sorted(glob.glob(str(RESULTS / f"{tag}_s*.json"))):
        with open(path) as handle:
            row = json.load(handle)
        if "Error" not in row:
            rows.append(row)
    return sorted(rows, key=lambda r: (r.get("UpdateRatePerInstrument", 0), r["RequestedSubscribers"]))


def table(rows):
    header = ("| Subscribers | Feed (upd/s) | Fan-out (msg/s) | Mean (ms) | p50 | p99 | p99.9 | Max | "
              "Delivered | Server CPU | Host CPU | Sustained |")
    lines = [header, "|" + "---|" * 12]
    for row in rows:
        lines.append(
            f"| {row['RequestedSubscribers']:,} "
            f"| {row.get('AggregateUpdateRate', 0):g} "
            f"| {row['MessagesPerSecond']:,.0f} "
            f"| **{row['MeanMs']:.2f}** "
            f"| {row['P50Ms']:.2f} "
            f"| {row['P99Ms']:.1f} "
            f"| {row['P999Ms']:.1f} "
            f"| {row['MaxMs']:.1f} "
            f"| {(row.get('DeliveryRatio') or 0) * 100:.1f}% "
            f"| {row.get('ServerCpuPercent')}% "
            f"| {row.get('HostCpuPercent')}% "
            f"| {'yes' if sustained(row) else 'NO'} |")
    return "\n".join(lines)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("tags", nargs="+")
    args = parser.parse_args()

    for tag in args.tags:
        rows = load(tag)
        if not rows:
            print(f"\n## {tag}\n\n(no results)\n")
            continue
        print(f"\n## {tag}\n")
        print(table(rows))
        best = [r for r in rows if sustained(r)]
        if best:
            top = max(best, key=lambda r: r["RequestedSubscribers"])
            print(f"\nHighest sustained: {top['RequestedSubscribers']:,} subscribers, "
                  f"{top['MessagesPerSecond']:,.0f} msg/s, {top['MeanMs']:.2f} ms mean, "
                  f"{top['P99Ms']:.1f} ms p99")


if __name__ == "__main__":
    main()
