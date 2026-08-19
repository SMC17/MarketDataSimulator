#!/usr/bin/env python3
"""
Sweeps the multicast feed across subscriber counts.

The unicast benchmark showed mean latency rising roughly linearly with the
subscriber population, because the server performs one write per subscriber per
update. Multicast sends once regardless, so the prediction under test is a
latency curve that does not depend on the number of subscribers.
"""
import argparse
import json
import os
import re
import signal
import subprocess
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SERVER_DLL = ROOT / "Server/bin/Release/net8.0/Server.dll"
BENCH_DLL = ROOT / "Bench/bin/Release/net8.0/Bench.dll"
RESULTS = ROOT / "bench/results"
CONFIGS = ROOT / "bench/configs"

ENV = dict(os.environ, DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_NOLOGO="1")

STATS_RE = re.compile(
    r"STATS clients=(\d+) queue=(-?\d+) peakQueue=(-?\d+) published/s=(\d+) "
    r"disseminated/s=(\d+) sent/s=(\d+) dropped=(\d+) failed=(\d+) "
    r"outQueued=(\d+) outMax=(\d+)")


def boot_id():
    """Identifies the running kernel instance, so a host swap mid-sweep is visible."""
    try:
        with open("/proc/sys/kernel/random/boot_id") as handle:
            return handle.read().strip()
    except OSError:
        return None


def host_cpu_sample():
    with open("/proc/stat") as handle:
        fields = [int(v) for v in handle.readline().split()[1:]]
    idle = fields[3] + fields[4]
    total = sum(fields)
    return total - idle, total


def process_cpu(pid):
    try:
        with open(f"/proc/{pid}/stat") as handle:
            fields = handle.read().rsplit(") ", 1)[1].split()
        return int(fields[11]) + int(fields[12])
    except (FileNotFoundError, ProcessLookupError, IndexError):
        return None


def run_case(args, subscribers, rate):
    label = f"{args.tag}_s{subscribers}_r{rate:g}"
    RESULTS.mkdir(parents=True, exist_ok=True)
    CONFIGS.mkdir(parents=True, exist_ok=True)

    run_for = args.warmup + args.duration + max(30, subscribers / 200) + 20
    config_path = CONFIGS / f"{label}.json"
    config_path.write_text(json.dumps({
        "Port": 14000,
        "VerboseLogging": False,
        "StatisticsIntervalSeconds": 1,
        "RunForSeconds": run_for,
        "BookImplementation": args.book,
        "PriceBand": 512,
        "Multicast": {
            "Enabled": True,
            "Group": args.group,
            "Port": args.port,
            "Interface": "127.0.0.1",
            "MaxBatch": args.max_batch,
            "FlushIntervalMs": args.flush_interval_ms,
            "SnapshotIntervalSeconds": 1.0,
        },
        "Instruments": [
            {"Id": i + 1, "Symbol": f"SYM{i + 1}",
             "Specifications": {"Depth": args.depth, "UpdatesPerSecond": rate, "SnapshotProbability": 0.05}}
            for i in range(args.instruments)
        ],
    }, indent=2))

    server_log = RESULTS / f"{label}.server.log"
    log_handle = server_log.open("w")
    server = subprocess.Popen(["dotnet", str(SERVER_DLL), str(config_path)],
                              cwd=str(SERVER_DLL.parent), env=ENV,
                              stdout=log_handle, stderr=subprocess.STDOUT)
    try:
        deadline = time.time() + 30
        while time.time() < deadline:
            if "Publishing to multicast" in server_log.read_text():
                break
            if server.poll() is not None:
                raise RuntimeError(f"server exited early: {server_log.read_text()[-2000:]}")
            time.sleep(0.2)
        else:
            raise RuntimeError("server did not start publishing")

        out_path = RESULTS / f"{label}.json"
        bench = subprocess.Popen([
            "dotnet", str(BENCH_DLL), "multicast",
            "--subscribers", str(subscribers),
            "--group", args.group, "--port", str(args.port),
            "--warmup", str(args.warmup), "--duration", str(args.duration),
            "--receive-buffer", str(args.receive_buffer),
            "--label", label, "--out", str(out_path),
        ], cwd=str(BENCH_DLL.parent), env=ENV,
            stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)

        host_busy0, host_total0 = host_cpu_sample()
        server_cpu0 = process_cpu(server.pid)

        while bench.poll() is None:
            time.sleep(0.5)

        host_busy1, host_total1 = host_cpu_sample()
        server_cpu1 = process_cpu(server.pid)
        output = bench.stdout.read()

        if bench.returncode != 0:
            raise RuntimeError(f"bench failed ({bench.returncode}):\n{output[-3000:]}")

        cores = os.cpu_count()
        clock = os.sysconf("SC_CLK_TCK")
        wall = (host_total1 - host_total0) / clock / cores
        result = json.loads(out_path.read_text())
        result["ServerCpuPercent"] = (
            round((server_cpu1 - server_cpu0) / clock / wall * 100, 1) if server_cpu1 and wall > 0 else None)
        result["HostCpuPercent"] = round(
            (host_busy1 - host_busy0) / max(1, host_total1 - host_total0) * 100 * cores, 1)
        result["HostCores"] = cores
        # See bench/environment.py: a sweep that changes host mid-way must not be
        # quotable as a single measurement session.
        result["HostBootId"] = boot_id()
        result["UpdateRatePerInstrument"] = rate
        result["AggregateUpdateRate"] = rate * args.instruments
        result["MaxBatch"] = args.max_batch
        result["BenchOutput"] = output.strip().splitlines()[-4:]
    finally:
        server.send_signal(signal.SIGINT)
        try:
            server.wait(timeout=15)
        except subprocess.TimeoutExpired:
            server.kill()
            server.wait(timeout=10)
        log_handle.close()

    stats = [tuple(int(v) for v in m.groups()) for m in STATS_RE.finditer(server_log.read_text())]
    # sent/s is packets per second here: with multicast it should track the update rate and be
    # entirely independent of how many subscribers are listening.
    result["ServerPacketsPerSecond"] = (
        round(sum(s[5] for s in stats) / len(stats), 1) if stats else None)
    # The rate the engine actually produced, so delivery can be judged against what
    # existed rather than against the rate the harness was configured for. On a
    # slower host the generator undershoots, and dividing by the nominal rate would
    # book that as subscriber loss.
    result["ServerMeanDisseminatedPerSecond"] = (
        round(sum(s[4] for s in stats) / len(stats), 1) if stats else None)
    result["ServerDroppedUpdates"] = max((s[6] for s in stats), default=None)
    out_path.write_text(json.dumps(result, indent=2))
    return result


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--subscribers", type=int, nargs="+", required=True)
    parser.add_argument("--rates", type=float, nargs="+", default=[50.0])
    parser.add_argument("--instruments", type=int, default=2)
    parser.add_argument("--depth", type=int, default=10)
    parser.add_argument("--book", default="SortedArray")
    parser.add_argument("--group", default="239.7.7.7")
    parser.add_argument("--port", type=int, default=31007)
    parser.add_argument("--max-batch", type=int, default=1)
    parser.add_argument("--flush-interval-ms", type=float, default=0.0)
    parser.add_argument("--receive-buffer", type=int, default=256 * 1024)
    parser.add_argument("--warmup", type=float, default=6)
    parser.add_argument("--duration", type=float, default=20)
    parser.add_argument("--tag", default="mcast")
    args = parser.parse_args()

    # A batch limit with no flush deadline is not a smaller batch - it is no batching
    # at all. The publisher flushes on every update when the interval is zero, so
    # --max-batch is silently inert and the sweep measures the same configuration
    # four times over while appearing to vary it. Refuse it rather than produce a
    # table that looks like a result.
    if args.max_batch > 1 and args.flush_interval_ms <= 0:
        parser.error(
            "--max-batch > 1 requires --flush-interval-ms > 0; with no flush deadline the "
            "publisher sends every update immediately and the batch limit is never reached")

    results = []
    for rate in args.rates:
        for subscribers in args.subscribers:
            print(f"\n=== {args.tag}: {subscribers} subscribers @ {rate:g} upd/s/instrument ===", flush=True)
            try:
                r = run_case(args, subscribers, rate)
            except Exception as exc:
                print(f"  FAILED: {exc}", flush=True)
                results.append({"Label": f"{args.tag}_s{subscribers}_r{rate:g}", "Error": str(exc)})
                continue

            print(f"  msgs/s={r['MessagesPerSecond']:,.0f} mean={r['MeanMs']}ms p50={r['P50Ms']}ms "
                  f"p99={r['P99Ms']}ms max={r['MaxMs']}ms gaps={r['Gaps']} missed={r['MissedMessages']} "
                  f"stale={r['StaleSubscribers']} pkts/s={r['ServerPacketsPerSecond']} "
                  f"srvCpu={r['ServerCpuPercent']}% hostCpu={r['HostCpuPercent']}%", flush=True)
            results.append(r)
            time.sleep(3)

    (RESULTS / f"{args.tag}-summary.json").write_text(json.dumps(results, indent=2))
    print(f"\nWrote {RESULTS / f'{args.tag}-summary.json'}")


if __name__ == "__main__":
    main()
