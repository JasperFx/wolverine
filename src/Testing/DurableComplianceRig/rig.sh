#!/usr/bin/env bash
# Durable receiver compliance rig. NOT part of either solution or CI.
#
#   ./rig.sh                         every feature
#   ./rig.sh "*settling_postgresql*" features whose class matches the filter
#
# Environment:
#   RIG_SUFFIX=abc          keeps parallel runs apart (schemas, queues, tenant databases)
#   RIG_SETTLE_SECONDS=240  overrides the default 60s wait for a run to settle
#   RIG_SKIP_LATE_DISPOSE=1 diagnostic for R1 (see DURABLE-COMPLIANCE-FINDINGS)
#
# Needs: docker compose up -d postgresql sqlserver rabbitmq localstack
set -uo pipefail
cd "$(dirname "$0")"

filter="${1:-*}"
logs="${RIG_LOGS:-./rig-logs}"
mkdir -p "$logs"
log="$logs/run-$(date +%Y%m%d-%H%M%S)${RIG_SUFFIX:+-$RIG_SUFFIX}.log"

export MSBUILDDISABLENODEREUSE=1
dotnet build -f net9.0 >"$log.build" 2>&1 || { echo "build failed, see $log.build"; exit 1; }

dotnet test --project . -f net9.0 --no-build --filter-class "$filter" >"$log" 2>&1
status=$?

grep -E '^  (total|succeeded|failed):' "$log"
grep -E '^failed ' "$log" | sed 's/^failed /  RED   /'
echo "full log: $log"
exit $status
