#!/usr/bin/env bash
# GH-4026 Pulsar cells — same shape as cells-rabbit.sh, pulsar harness.
# Requires: docker compose up -d pulsar postgresql

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

# --- max-throughput cells (uncapped publisher, no handler work) ---

# RIG_PUBLISHERS=32: one awaited DotPulsar produce is ~2ms, so a single loop tops out around 450/s and
# publisher-bounds every cell (measured 2026-08-23: buffered, durable-1 and durable all ~450/s with
# published == consumed). 32 concurrent loops push the publisher well past the consumer.
PMAX="RIG_SMALL_RATE=-1 RIG_LARGE_RATE=0 RIG_HANDLER_MS=0 RIG_SEQ=none RIG_SEND_MODE=inline RIG_PUBLISHERS=32 RIG_WARMUP_S=15 RIG_DURATION_S=45"

run_cell p-max-buffered    pulsar $PMAX RIG_MODE=buffered
# GH-4026: -1 pins MaximumMessagesToReceive=1, which is exactly the pre-GH-4026 Pulsar durable path
# (one message per inbox insert); p-max-durable is the 5ms / MaximumMessagesToReceive window.
run_cell p-max-durable-1   pulsar $PMAX RIG_MODE=durable RIG_MAX_RECEIVE=1
run_cell p-max-durable     pulsar $PMAX RIG_MODE=durable
