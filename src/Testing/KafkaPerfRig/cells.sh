#!/usr/bin/env bash
# GH-3490 local experiment sweep. Each cell is one rig.sh run; strictly sequential so cells
# never share broker/CPU. Client-shaped latency cells first, then high-rate throughput cells.
#
#   ./cells.sh            # run everything
#   ./cells.sh baseline native-anchor   # run selected cells by name

set -euo pipefail
cd "$(dirname "$0")"

WARMUP="${CELL_WARMUP:-20}"
DURATION="${CELL_DURATION:-120}"

run_cell() {
  local name="$1" harness="$2"
  shift 2
  if [[ "${SELECTED:-0}" == "1" ]] && ! printf '%s\n' "${CELLS[@]}" | grep -qx "$name"; then
    return 0
  fi
  echo ""
  echo "=================== CELL: $name ==================="
  RIG_WARMUP_S="$WARMUP" RIG_DURATION_S="$DURATION" ./rig.sh "$harness" "$name" "$@" || echo "[cells] $name FAILED"
}

if [[ $# -gt 0 ]]; then
  SELECTED=1
  CELLS=("$@")
else
  SELECTED=0
  CELLS=()
fi

# --- client-shaped latency cells (1Kb @8/s + 100Kb @0.6/s, 9ms handler) ---

# The client's own configuration: buffered, batch(10,10ms), semaphore sequencing
run_cell baseline           wolverine

# The comparison anchor: raw Confluent.Kafka twin, same corpus + handler sim
run_cell native-anchor      native

# T1: Wolverine's actual defaults — batch(100,250ms) debounce
run_cell batch-default      wolverine RIG_BATCH_SIZE=100 RIG_BATCH_TIMEOUT_MS=250

# T1: batching effectively off
run_cell batch-1-1          wolverine RIG_BATCH_SIZE=1 RIG_BATCH_TIMEOUT_MS=1

# T5: inline sender (per-message ProduceAsync + Flush bug in play)
run_cell send-inline        wolverine RIG_SEND_MODE=inline

# T4: durable inbox (Postgres)
run_cell mode-durable       wolverine RIG_MODE=durable

# E1: inline processing on the consume loop
run_cell mode-inline        wolverine RIG_MODE=inline

# T2: no sequencing gate inside the handler
run_cell seq-none           wolverine RIG_SEQ=none

# --- throughput cells (2000/s small only, no handler work, no sequencing) ---

THRU="RIG_SMALL_RATE=2000 RIG_LARGE_RATE=0 RIG_HANDLER_MS=0 RIG_SEQ=none RIG_BATCH_SIZE=100 RIG_BATCH_TIMEOUT_MS=250"

run_cell thru-buffered      wolverine $THRU
run_cell thru-native        native    $THRU
run_cell thru-durable       wolverine $THRU RIG_MODE=durable
run_cell thru-inline        wolverine $THRU RIG_MODE=inline

# --- max-throughput cells (uncapped publisher; consumed_per_sec is the metric) ---

MAX="RIG_SMALL_RATE=-1 RIG_LARGE_RATE=0 RIG_HANDLER_MS=0 RIG_SEQ=none RIG_BATCH_SIZE=100 RIG_BATCH_TIMEOUT_MS=250 RIG_WARMUP_S=15 RIG_DURATION_S=45"

run_cell max-buffered       wolverine $MAX
run_cell max-native         native    $MAX
run_cell max-durable        wolverine $MAX RIG_MODE=durable

echo ""
echo "[cells] sweep complete. Summaries:"
for f in rig-results/*/*-summary.json; do
  echo "--- $f"
done
