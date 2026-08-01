#!/usr/bin/env bash
#
# Samples memory pressure to stdout while a CI test target runs.
#
# Why stdout and not a file: the failure this exists to diagnose (GH-3771) is the *runner itself*
# being killed ~15 minutes in. When that happens every later step is skipped -- the observed run had
# "Stop containers: skipped" -- so an artifact upload or an `if: failure()` dmesg dump never
# executes. Anything already streamed to the live job log, on the other hand, survives. So the
# sampler has to run in the background of the step being diagnosed and print as it goes.
#
# Each line is prefixed [mem] so the curve can be pulled out of a noisy log with a single grep:
#
#   gh run view <id> --log | grep '\[mem\]'
#
# Reads as: if MemAvailable trends toward zero and swap fills just before the kill, this is the OOM
# killer taking the runner service and the fix is footprint (lane count, per-lane hosts). If memory
# is flat at the moment of death, it is not OOM and lane tuning would be treating the wrong thing.

set -uo pipefail

interval="${MEMORY_SAMPLE_INTERVAL:-15}"

echo "[mem] sampling every ${interval}s -- total/used/free/available in MB, plus the largest RSS consumers"

while true; do
    # `free -m` line 2 is the physical memory row; column 7 is "available", which is the number that
    # actually predicts an OOM kill (unlike "free", which excludes reclaimable page cache).
    read -r _ total used free _ _ available <<<"$(free -m | awk 'NR==2')"
    swap_used="$(free -m | awk 'NR==3 {print $3}')"

    # Top three processes by RSS, so a spike can be attributed to a test host / Postgres / the runner
    # rather than just observed in aggregate.
    top="$(ps -eo rss=,comm= --sort=-rss | head -3 | awk '{printf "%s=%dMB ", $2, $1/1024}')"

    echo "[mem] $(date -u +%H:%M:%S) total=${total} used=${used} free=${free} available=${available} swap_used=${swap_used} | ${top}"

    sleep "${interval}"
done
