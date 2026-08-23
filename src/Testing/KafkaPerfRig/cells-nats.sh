#!/usr/bin/env bash
# GH-4026 NATS JetStream cells — same shape as cells-rabbit.sh, nats harness.
# Requires: docker compose up -d nats postgresql

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

NMAX="RIG_SMALL_RATE=-1 RIG_LARGE_RATE=0 RIG_HANDLER_MS=0 RIG_SEQ=none RIG_SEND_MODE=inline RIG_WARMUP_S=15 RIG_DURATION_S=45"

run_cell n-max-buffered    nats $NMAX RIG_MODE=buffered
# GH-4026: -1 pins MaximumMessagesToReceive=1, which is exactly the pre-GH-4026 JetStream durable path
# (one message per inbox insert); n-max-durable is the 5ms / MaximumMessagesToReceive window.
run_cell n-max-durable-1   nats $NMAX RIG_MODE=durable RIG_MAX_RECEIVE=1
run_cell n-max-durable     nats $NMAX RIG_MODE=durable
