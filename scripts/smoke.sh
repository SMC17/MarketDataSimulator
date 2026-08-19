#!/usr/bin/env bash
# End-to-end smoke test: start the simulator on each transport and confirm a subscriber
# actually receives a coherent feed. Fast enough for CI, and it catches the class of
# failure where everything builds and nothing works.
set -euo pipefail

cd "$(dirname "$0")/.."
export DOTNET_ROLL_FORWARD=Major DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"; jobs -p | xargs -r kill 2>/dev/null || true' EXIT

dotnet build Server/Server.csproj -c Release --nologo -v quiet
dotnet build Bench/Bench.csproj -c Release --nologo -v quiet

server() {
  ( cd Server/bin/Release/net6.0 && dotnet Server.dll "$1" > "$WORK/server.log" 2>&1 & )
  for _ in $(seq 1 60); do
    grep -qE "Listening on|Publishing to multicast" "$WORK/server.log" && return 0
    sleep 0.5
  done
  echo "server failed to start:"; cat "$WORK/server.log"; return 1
}

stop() { pkill -f "Server.dll" 2>/dev/null || true; sleep 1; }

# ---- unicast gRPC ----
cat > "$WORK/unicast.json" <<JSON
{ "Port": 14311, "VerboseLogging": false, "StatisticsIntervalSeconds": 0, "RunForSeconds": 40,
  "Instruments": [ { "Id": 1, "Symbol": "SMOKE",
    "Specifications": { "Depth": 10, "UpdatesPerSecond": 200, "SnapshotProbability": 0.05 } } ] }
JSON
server "$WORK/unicast.json"
( cd Bench/bin/Release/net6.0 && dotnet Bench.dll --address http://127.0.0.1:14311 \
    --subscribers 5 --instruments 1 --warmup 2 --duration 4 --out "$WORK/unicast.json.out" )
stop

python3 - "$WORK/unicast.json.out" <<'PY'
import json, sys
r = json.load(open(sys.argv[1]))
assert r["ConnectedSubscribers"] == 5, r
assert r["MessagesReceived"] > 0, "unicast subscribers received nothing"
assert r["MeanMs"] > 0, r
print(f"unicast OK: {r['MessagesReceived']} msgs, mean {r['MeanMs']} ms")
PY

# ---- multicast ----
cat > "$WORK/multicast.json" <<JSON
{ "Port": 14312, "VerboseLogging": false, "StatisticsIntervalSeconds": 0, "RunForSeconds": 40,
  "Multicast": { "Enabled": true, "Group": "239.7.7.9", "Port": 31009, "Interface": "127.0.0.1",
                 "MaxBatch": 1, "FlushIntervalMs": 0, "SnapshotIntervalSeconds": 1.0 },
  "Instruments": [ { "Id": 1, "Symbol": "SMOKE",
    "Specifications": { "Depth": 10, "UpdatesPerSecond": 200, "SnapshotProbability": 0.05 } } ] }
JSON
server "$WORK/multicast.json"
( cd Bench/bin/Release/net6.0 && dotnet Bench.dll multicast --group 239.7.7.9 --port 31009 \
    --subscribers 5 --warmup 2 --duration 4 --out "$WORK/multicast.json.out" )
stop

python3 - "$WORK/multicast.json.out" <<'PY'
import json, sys
r = json.load(open(sys.argv[1]))
assert r["MessagesReceived"] > 0, "multicast subscribers received nothing"
assert r["Malformed"] == 0, r
assert r["StaleSubscribers"] == 0, r
print(f"multicast OK: {r['MessagesReceived']} msgs, mean {r['MeanMs']} ms, "
      f"{r['Gaps']} gaps, {r['StaleSubscribers']} stale")
PY

echo "smoke: all transports OK"
