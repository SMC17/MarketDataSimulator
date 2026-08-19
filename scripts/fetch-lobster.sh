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
BASE="https://media.githubusercontent.com/media/kpetridis24/lobsim/master/sample_data"

mkdir -p "$DEST"

for f in \
  "AMZN_2012-06-21_34200000_57600000_message_10.csv" \
  "AMZN_2012-06-21_34200000_57600000_orderbook_10.csv"
do
  if [ -s "$DEST/$f" ]; then
    echo "have $f"
    continue
  fi
  echo "fetching $f ..."
  curl -sSfL --max-time 900 -o "$DEST/$f" "$BASE/$f"
done

echo
echo "message rows:   $(wc -l < "$DEST/AMZN_2012-06-21_34200000_57600000_message_10.csv")"
echo "reference rows: $(wc -l < "$DEST/AMZN_2012-06-21_34200000_57600000_orderbook_10.csv")"
echo
echo "run: dotnet run --project Bench -c Release -- replay --data $DEST"
