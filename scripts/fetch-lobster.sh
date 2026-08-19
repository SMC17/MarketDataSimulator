#!/usr/bin/env bash
# Fetches the full LOBSTER sample session (AMZN, 2012-06-21, level 10) used by the replay
# benchmark. Roughly 71 MiB, so it is not committed; a 20,000-message slice lives in
# data/sample/ and is what the test suite replays.
#
# LOBSTER derives these files from NASDAQ's ITCH feed. Sample data is published at
# lobsterdata.com; this mirror is used because it is reachable without a login.
set -euo pipefail

cd "$(dirname "$0")/.."
DEST="data/lobster"
mkdir -p "$DEST"

# Three instruments on the same session, chosen for how differently they trade:
#   AMZN  ~$223, 13-tick spread, 10 levels published
#   GOOG  ~$571, 30-tick spread,  5 levels
#   MSFT   ~$31,  1.3-tick spread (tick-constrained), 5 levels, 596k messages
fetch() { # repo-path, filename
  local url="$1" f="$2"
  if [ -s "$DEST/$f" ]; then echo "have $f"; return; fi
  echo "fetching $f ..."
  curl -sSfL --max-time 900 -o "$DEST/$f" "$url/$f"
}

LOBSIM="https://media.githubusercontent.com/media/kpetridis24/lobsim/master/sample_data"
MMVIARL="https://media.githubusercontent.com/media/asarfa/MMviaRL/main/data"

fetch "$LOBSIM"  "AMZN_2012-06-21_34200000_57600000_message_10.csv"
fetch "$LOBSIM"  "AMZN_2012-06-21_34200000_57600000_orderbook_10.csv"
fetch "$MMVIARL" "GOOG_2012-06-21_34200000_57600000_message_5.csv"
fetch "$MMVIARL" "GOOG_2012-06-21_34200000_57600000_orderbook_5.csv"
fetch "$MMVIARL" "MSFT_2012-06-21_34200000_57600000_message_5.csv"
fetch "$MMVIARL" "MSFT_2012-06-21_34200000_57600000_orderbook_5.csv"

echo
for f in "$DEST"/*_message_*.csv; do
  echo "$(basename "$f"): $(wc -l < "$f") messages"
done
echo
echo "run: dotnet run --project Bench -c Release -- replay --data $DEST"
echo "     dotnet run --project Bench -c Release -- study  --data $DEST"
