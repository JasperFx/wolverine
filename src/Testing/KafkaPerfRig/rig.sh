#!/usr/bin/env bash
# Run controller for the GH-3490 Kafka perf rig.
#
#   ./rig.sh <harness> <scenario-name> [extra env assignments...]
#
#   harness:  wolverine | native
#
# Scenario knobs are plain RIG_* env vars (see RigConfig.cs); anything passed after the
# scenario name is exported verbatim, e.g.:
#
#   RIG_DURATION_S=120 ./rig.sh wolverine baseline RIG_BATCH_SIZE=10 RIG_BATCH_TIMEOUT_MS=10
#
# Results land in rig-results/<scenario>-<timestamp>/ next to this script.

set -euo pipefail
cd "$(dirname "$0")"

HARNESS="${1:?usage: rig.sh <wolverine|native> <scenario-name> [ENV=VAL...]}"
SCENARIO="${2:?scenario name required}"
shift 2

for assignment in "$@"; do
  export "${assignment?}"
done

export RIG_RUN_ID="${SCENARIO}-$(date +%H%M%S)"
export RIG_OUT="$(pwd)/rig-results/${RIG_RUN_ID}"
mkdir -p "$RIG_OUT"

WARMUP="${RIG_WARMUP_S:-30}"
DURATION="${RIG_DURATION_S:-120}"
DRAIN="${RIG_DRAIN_S:-15}"

BIN="bin/Release/net9.0/KafkaPerfRig"
if [[ ! -x "$BIN" ]]; then
  dotnet build -c Release --nologo -v q
fi

echo "[rig.sh] run $RIG_RUN_ID: harness=$HARNESS warmup=${WARMUP}s duration=${DURATION}s"
env | grep '^RIG_' | sort | tee "$RIG_OUT/config.env"

"$BIN" "${HARNESS}-consumer" >"$RIG_OUT/consumer.log" 2>&1 &
CONSUMER_PID=$!
trap 'kill $CONSUMER_PID 2>/dev/null || true' EXIT

# let the consumer join its group / provision topics before traffic starts
sleep 10

"$BIN" "${HARNESS}-publisher" >"$RIG_OUT/publisher.log" 2>&1

echo "[rig.sh] publisher finished; draining ${DRAIN}s"
sleep "$DRAIN"

kill -TERM "$CONSUMER_PID"
wait "$CONSUMER_PID" || true
trap - EXIT

echo "[rig.sh] done. results:"
ls -l "$RIG_OUT"
grep -A 100 '"scenario"' "$RIG_OUT/consumer.log" | tail -60 || true
