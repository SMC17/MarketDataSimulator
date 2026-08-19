#!/usr/bin/env python3
"""
Records the host a measurement session ran on, into bench/results/environment.json.

This is written once per measurement session and read by bench/docgen.py, rather
than being probed when the documentation is generated. The distinction matters:
the environment table describes the machine the numbers came from, and probing it
at generation time would silently relabel someone else's results with the CPU of
whoever last ran the generator. A benchmark session that moved to a different host
mid-way is exactly how a document ends up quoting two machines as one.
"""
import json
import os
import platform
import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
RESULTS = ROOT / "bench/results"


def boot_id():
    try:
        with open("/proc/sys/kernel/random/boot_id") as handle:
            return handle.read().strip()
    except OSError:
        return None


def cpu_model():
    try:
        with open("/proc/cpuinfo") as handle:
            for line in handle:
                if line.startswith("model name"):
                    return line.split(":", 1)[1].strip()
    except OSError:
        pass
    return platform.processor() or "unknown"


def cpu_flags(interesting):
    try:
        with open("/proc/cpuinfo") as handle:
            for line in handle:
                if line.startswith("flags"):
                    present = set(line.split(":", 1)[1].split())
                    return [flag for flag in interesting if flag in present]
    except OSError:
        pass
    return []


def memory_gb():
    try:
        with open("/proc/meminfo") as handle:
            return round(int(handle.readline().split()[1]) / 1024 / 1024, 1)
    except (OSError, ValueError, IndexError):
        return None


def os_name():
    try:
        with open("/etc/os-release") as handle:
            for line in handle:
                if line.startswith("PRETTY_NAME="):
                    return line.split("=", 1)[1].strip().strip('"')
    except OSError:
        pass
    return platform.system()


def run(command):
    try:
        return subprocess.run(command, capture_output=True, text=True, timeout=120).stdout.strip()
    except (OSError, subprocess.SubprocessError):
        return None


def runtime_version():
    info = run(["dotnet", "--info"])
    if info:
        match = re.search(r"Host:.*?Version:\s*([0-9][^\s]*)", info, re.DOTALL)
        if match:
            return match.group(1)
    return run(["dotnet", "--version"])


def main():
    RESULTS.mkdir(parents=True, exist_ok=True)
    path = RESULTS / "environment.json"

    environment = {
        # Identifies the running kernel instance. Benchmark sessions here run in
        # containers that can be replaced between phases, and a session that moves
        # host mid-way produces a document quoting two machines as one - which is
        # not a hypothetical, it is how an earlier revision of BENCHMARKS.md came to
        # mix a 2.10GHz host and a 2.80GHz one in a single table. Every result file
        # carries this, and bench/docgen.py refuses to render a document from
        # results that disagree about it.
        "BootId": boot_id(),
        "Cpu": cpu_model(),
        "Cores": os.cpu_count(),
        "CpuFeatures": cpu_flags([
            "avx2", "avx512f", "avx512bw", "avx512dq", "avx512vl", "bmi1", "bmi2", "popcnt",
        ]),
        "MemoryGb": memory_gb(),
        "Os": os_name(),
        "Kernel": platform.release(),
        "DotnetRuntime": runtime_version(),
        "ServerGarbageCollector": True,
    }

    path.write_text(json.dumps(environment, indent=2) + "\n")
    print(f"Wrote {path}")
    for key, value in environment.items():
        print(f"  {key}: {value}")


if __name__ == "__main__":
    sys.exit(main())
