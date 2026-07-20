#!/usr/bin/env bash
# GH-3492 RabbitMQ experiment sweep — same shape as cells.sh, rabbit/native-rabbit harnesses.
# Requires: docker compose up -d rabbitmq postgresql

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

# Wolverine RabbitMQ out-of-the-box: Inline listeners, default sender batching (R1 shape)
run_cell r-default        rabbit        RIG_MODE=default RIG_SEND_MODE=default RIG_SEQ=none

# native anchor
run_cell r-native         native-rabbit RIG_SEQ=none

# batching latency: Wolverine defaults (100,250ms debounce pre-fix) vs effectively-off.
# NOTE: Rabbit subscribers default to Inline sending, so batch knobs without BufferedInMemory
# are inert — r-batch-buffered is the cell that actually exercises the BatchedSender.
run_cell r-batch-default  rabbit        RIG_MODE=default RIG_SEND_MODE=batched RIG_BATCH_SIZE=100 RIG_BATCH_TIMEOUT_MS=250 RIG_SEQ=none
run_cell r-batch-1-1      rabbit        RIG_MODE=default RIG_SEND_MODE=batched RIG_BATCH_SIZE=1 RIG_BATCH_TIMEOUT_MS=1 RIG_SEQ=none
run_cell r-batch-buffered rabbit        RIG_MODE=default RIG_SEND_MODE=buffered RIG_BATCH_SIZE=100 RIG_BATCH_TIMEOUT_MS=250 RIG_SEQ=none

# --- R1 throughput: inline single-file vs parallelism levers (2000/s, 5ms handler) ---

RTHRU="RIG_SMALL_RATE=2000 RIG_LARGE_RATE=0 RIG_HANDLER_MS=5 RIG_SEQ=none RIG_SEND_MODE=default"

run_cell r-thru-inline-1   rabbit        $RTHRU RIG_MODE=default
run_cell r-thru-inline-4   rabbit        $RTHRU RIG_MODE=default RIG_LISTENER_COUNT=4
run_cell r-thru-buffered   rabbit        $RTHRU RIG_MODE=buffered
run_cell r-thru-durable    rabbit        $RTHRU RIG_MODE=durable
run_cell r-thru-native     native-rabbit $RTHRU

# --- max-throughput cells (uncapped publisher, no handler work) ---

RMAX="RIG_SMALL_RATE=-1 RIG_LARGE_RATE=0 RIG_HANDLER_MS=0 RIG_SEQ=none RIG_SEND_MODE=default RIG_WARMUP_S=15 RIG_DURATION_S=45"

run_cell r-max-inline      rabbit        $RMAX RIG_MODE=default
run_cell r-max-buffered    rabbit        $RMAX RIG_MODE=buffered
run_cell r-max-durable     rabbit        $RMAX RIG_MODE=durable
run_cell r-max-native      native-rabbit $RMAX

echo ""
echo "[cells-rabbit] sweep complete."
