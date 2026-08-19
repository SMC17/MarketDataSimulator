#!/usr/bin/env python3
"""
Drives the dissemination benchmark: starts the simulator, ramps a subscriber
population against it, and records latency together with enough server- and
host-side telemetry to tell a real result from a measurement artefact.

Each case is a fresh server process, so runs cannot contaminate each other.
"""
import argparse
import json
import os
import re
import signal
import subprocess
import sys
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
    """Aggregate jiffies from /proc/stat -> (busy, total)."""
    with open("/proc/stat") as handle:
        fields = [int(v) for v in handle.readline().split()[1:]]
    idle = fields[3] + fields[4]
    total = sum(fields)
    return total - idle, total


def process_cpu(pid):
    """Total (utime+stime) jiffies for a pid, or None once it is gone."""
    try:
        with open(f"/proc/{pid}/stat") as handle:
            fields = handle.read().rsplit(") ", 1)[1].split()
        return int(fields[11]) + int(fields[12])
    except (FileNotFoundError, ProcessLookupError, IndexError):
        return None


def process_rss_mb(pid):
    try:
        with open(f"/proc/{pid}/statm") as handle:
            return int(handle.read().split()[1]) * os.sysconf("SC_PAGE_SIZE") / 1024 / 1024
    except (FileNotFoundError, ProcessLookupError, IndexError):
        return None


def write_config(path, port, instruments, rate, depth, snapshot_probability, run_for, use_ring=False):
    config = {
        "Port": port,
        "VerboseLogging": False,
        "StatisticsIntervalSeconds": 1,
        "RunForSeconds": run_for,
        "UseRingQueue": use_ring,
        "Instruments": [
            {
                "Id": i + 1,
                "Symbol": f"SYM{i + 1}",
                "Specifications": {
                    "Depth": depth,
                    "UpdatesPerSecond": rate,
                    "SnapshotProbability": snapshot_probability,
                },
            }
            for i in range(instruments)
        ],
    }
    path.write_text(json.dumps(config, indent=2))
    return config


def run_case(args, subscribers, rate, tag):
    label = f"{tag}_s{subscribers}_r{rate:g}"
    RESULTS.mkdir(parents=True, exist_ok=True)
    CONFIGS.mkdir(parents=True, exist_ok=True)

    ramp_budget = max(20, subscribers / 400)
    run_for = args.warmup + args.duration + ramp_budget + 20

    config_path = CONFIGS / f"{label}.json"
    config = write_config(config_path, args.port, args.instruments, rate,
                          args.depth, args.snapshot_probability, run_for, args.ring)

    server_log = RESULTS / f"{label}.server.log"
    log_handle = server_log.open("w")

    server = subprocess.Popen(
        ["dotnet", str(SERVER_DLL), str(config_path)],
        cwd=str(SERVER_DLL.parent), env=ENV, stdout=log_handle, stderr=subprocess.STDOUT)

    try:
        # Wait for the listener before ramping subscribers at it.
        deadline = time.time() + 30
        while time.time() < deadline:
            if "Listening on" in server_log.read_text():
                break
            if server.poll() is not None:
                raise RuntimeError(f"server exited early: {server_log.read_text()[-2000:]}")
            time.sleep(0.2)
        else:
            raise RuntimeError("server did not start listening")

        out_path = RESULTS / f"{label}.json"
        bench_cmd = [
            "dotnet", str(BENCH_DLL),
            "--address", f"http://127.0.0.1:{args.port}",
            "--subscribers", str(subscribers),
            "--instruments", ",".join(str(i + 1) for i in range(args.instruments)),
            "--subscribers-per-connection", str(args.subscribers_per_connection),
            "--warmup", str(args.warmup),
            "--duration", str(args.duration),
            "--connect-batch", str(args.connect_batch),
            "--connect-batch-delay-ms", str(args.connect_batch_delay_ms),
            "--label", label,
            "--out", str(out_path),
        ]

        bench = subprocess.Popen(bench_cmd, cwd=str(BENCH_DLL.parent), env=ENV,
                                 stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)

        # Sample host and server CPU for the life of the harness process.
        host_busy0, host_total0 = host_cpu_sample()
        server_cpu0 = process_cpu(server.pid)
        peak_server_rss = 0.0
        samples = 0

        while bench.poll() is None:
            time.sleep(0.5)
            samples += 1
            rss = process_rss_mb(server.pid)
            if rss:
                peak_server_rss = max(peak_server_rss, rss)

        host_busy1, host_total1 = host_cpu_sample()
        server_cpu1 = process_cpu(server.pid)

        bench_output = bench.stdout.read()
        if bench.returncode != 0:
            raise RuntimeError(f"bench failed ({bench.returncode}):\n{bench_output[-3000:]}")

        cores = os.cpu_count()
        host_cpu_pct = (host_busy1 - host_busy0) / max(1, host_total1 - host_total0) * 100 * cores
        clock = os.sysconf("SC_CLK_TCK")
        wall = (host_total1 - host_total0) / clock / cores
        server_cpu_pct = ((server_cpu1 - server_cpu0) / clock / wall * 100) if server_cpu1 and wall > 0 else None

        result = json.loads(out_path.read_text())

    finally:
        server.send_signal(signal.SIGINT)
        try:
            server.wait(timeout=15)
        except subprocess.TimeoutExpired:
            server.kill()
            server.wait(timeout=10)
        log_handle.close()

    # Reconcile against the server's own view: latency is only meaningful if the
    # fan-out actually kept up, so carry the queue watermark into the result.
    stats = [tuple(int(v) for v in m.groups()) for m in STATS_RE.finditer(server_log.read_text())]
    steady = [s for s in stats if s[0] >= result["ConnectedSubscribers"] * 0.99] or stats

    result.update({
        # Which kernel instance produced this run. Containers here can be replaced
        # between phases of a sweep, and a document that mixes two hosts in one
        # table is indistinguishable from one that does not unless every run says
        # where it came from. bench/docgen.py refuses to render across a mismatch.
        "HostBootId": boot_id(),
        "UpdateRatePerInstrument": rate,
        "Instruments": args.instruments,
        "AggregateUpdateRate": rate * args.instruments,
        "Depth": args.depth,
        "ServerCpuPercent": round(server_cpu_pct, 1) if server_cpu_pct else None,
        "ServerPeakRssMb": round(peak_server_rss, 1),
        "HostCpuPercent": round(host_cpu_pct, 1),
        "HostCores": cores,
        "ServerMaxQueueDepth": max((s[2] for s in steady), default=None),
        "ServerMaxSentPerSecond": max((s[5] for s in steady), default=None),
        "ServerMeanDisseminatedPerSecond": (
            round(sum(s[4] for s in steady) / len(steady), 1) if steady else None),
        "ServerDroppedUpdates": max((s[6] for s in steady), default=None),
        "ServerFailedSends": max((s[7] for s in steady), default=None),
        "ServerMaxOutboundQueued": max((s[9] for s in steady), default=None),
        "ServerMeanOutboundQueued": (
            round(sum(s[8] for s in steady) / len(steady), 1) if steady else None),
        "BenchOutput": bench_output.strip().splitlines()[-4:],
    })

    # Two different ratios, because two different things can go wrong.
    #
    # DeliveryRatio asks whether the fan-out delivered what the engine produced. It
    # divides by the rate the server actually published, read back from the server's
    # own telemetry.
    #
    # GeneratorRateFidelity asks whether the harness produced what it was asked to.
    # On a slower host the update generator undershoots its configured rate, and a
    # delivery ratio computed against the *nominal* rate books that shortfall as
    # subscriber loss - reporting the system as failing when the measuring
    # instrument is the thing falling short. Keeping them apart is what makes the
    # sustained/not-sustained judgement mean anything.
    connected = result["ConnectedSubscribers"]
    nominal = rate * args.instruments
    produced = result.get("ServerMeanDisseminatedPerSecond")

    result["NominalMessagesPerSecond"] = nominal * connected
    result["ExpectedMessagesPerSecond"] = round(produced * connected, 1) if produced else None
    result["DeliveryRatio"] = (
        round(result["MessagesPerSecond"] / (produced * connected), 4)
        if produced and connected else None)
    result["GeneratorRateFidelity"] = round(produced / nominal, 4) if produced and nominal else None
    out_path.write_text(json.dumps(result, indent=2))
    return result


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--subscribers", type=int, nargs="+", required=True)
    parser.add_argument("--rates", type=float, nargs="+", default=[50.0])
    parser.add_argument("--instruments", type=int, default=2)
    parser.add_argument("--depth", type=int, default=10)
    parser.add_argument("--snapshot-probability", type=float, default=0.05)
    parser.add_argument("--subscribers-per-connection", type=int, default=1)
    parser.add_argument("--warmup", type=float, default=8)
    parser.add_argument("--duration", type=float, default=30)
    parser.add_argument("--connect-batch", type=int, default=200)
    parser.add_argument("--connect-batch-delay-ms", type=float, default=25)
    parser.add_argument("--port", type=int, default=14000)
    parser.add_argument("--ring", action="store_true",
                        help="route the engine-to-fan-out hand-off through lock-free rings")
    parser.add_argument("--tag", default="sweep")
    parser.add_argument("--summary", default=None)
    args = parser.parse_args()

    results = []
    for rate in args.rates:
        for subscribers in args.subscribers:
            print(f"\n=== {args.tag}: {subscribers} subscribers @ {rate:g} upd/s/instrument ===", flush=True)
            try:
                result = run_case(args, subscribers, rate, args.tag)
            except Exception as exc:  # keep the sweep going; a failed point is itself a data point
                print(f"  FAILED: {exc}", flush=True)
                results.append({"Label": f"{args.tag}_s{subscribers}_r{rate:g}", "Error": str(exc)})
                continue

            print(f"  connected={result['ConnectedSubscribers']}/{result['RequestedSubscribers']} "
                  f"lost={result['FailedSubscribers']} "
                  f"msgs/s={result['MessagesPerSecond']:,.0f} "
                  f"mean={result['MeanMs']}ms p50={result['P50Ms']}ms p99={result['P99Ms']}ms "
                  f"max={result['MaxMs']}ms delivered={result['DeliveryRatio']:.3f} "
                  f"queue={result['ServerMaxQueueDepth']} outMax={result['ServerMaxOutboundQueued']} "
                  f"drops={result['ServerDroppedUpdates']} "
                  f"srvCpu={result['ServerCpuPercent']}% hostCpu={result['HostCpuPercent']}%", flush=True)
            results.append(result)
            time.sleep(3)  # let sockets drain out of TIME_WAIT between cases

    summary_path = Path(args.summary) if args.summary else RESULTS / f"{args.tag}-summary.json"
    summary_path.write_text(json.dumps(results, indent=2))
    print(f"\nWrote {summary_path}")


if __name__ == "__main__":
    main()
