#!/usr/bin/env bash
#
# Runs ./build.sh while relaying the runner's cancellation signal to the Nuke build process
# itself, so its SIGTERM handler (build/StallCapture.cs) gets to write the partial ledger and
# capture the wedged worker's async stacks before the hard kill.
#
# Why this exists: the runner signals only the step's own shell on cancellation, and nothing in
# the bash -> build.sh -> `dotnet run` chain forwards SIGTERM to the grandchild that registered
# the handler. Found live on the first capped job after the handler shipped: CIKafka wedged, the
# stall detector named the test and pid in the log — and the partial ledger never happened,
# because the signal never arrived. The build process publishes its pid to .nuke/temp/build.pid
# (build/StallCapture.cs), and this wrapper signals exactly that pid — deliberately NOT the
# process group, which would also kill the wedged worker the handler wants to dump.
#
# Usage (in a workflow step): ./build/run-with-cancellation-relay.sh <target> [args...]

set -uo pipefail

pid_file=".nuke/temp/build.pid"
rm -f "${pid_file}"

relay() {
  local pid
  pid=$(cat "${pid_file}" 2>/dev/null || true)
  if [ -n "${pid}" ] && kill -0 "${pid}" 2>/dev/null; then
    echo "[stall] relaying cancellation to the build process (pid ${pid})"
    kill -TERM "${pid}" 2>/dev/null || true
    # Keep the step alive while the handler writes the partial ledger and captures stacks.
    # The runner's own hard-kill deadline still bounds everything; this only stops the step
    # from exiting underneath the handler.
    for _ in $(seq 1 120); do kill -0 "${pid}" 2>/dev/null || break; sleep 1; done
  else
    echo "[stall] cancellation before the build process published a pid — nothing to relay"
  fi
}

trap relay INT TERM

./build.sh "$@" &
build_pid=$!
wait "${build_pid}"
status=$?
trap - INT TERM
exit "${status}"
