#!/usr/bin/env bash
#
# Downloads the last completed main run's published `test-ledger-run` artifact (aggregate.json +
# durations/) into the given directory, so runSupervised can feed Supervisor.KnownTestDurations —
# see build/TestDurations.cs for the whole loop. Best-effort by design: every failure path exits 0
# with nothing downloaded, and the build then balances lanes by test count exactly as before.
# Walks a few runs back rather than taking the immediately previous one, same as the flakiness
# baseline: a cancelled run publishes nothing, and a slightly older baseline beats none.
#
# Usage: fetch-duration-baseline.sh <dest-dir>
# Expects: GH_TOKEN (or a gh auth login), GITHUB_REPOSITORY; GITHUB_RUN_ID excluded when set.

set -uo pipefail

dest="${1:?usage: fetch-duration-baseline.sh <dest-dir>}"
repo="${GITHUB_REPOSITORY:-JasperFx/wolverine}"

candidates=$(gh api "repos/${repo}/actions/workflows/tests.yml/runs?branch=main&status=completed&per_page=10" \
  --jq '.workflow_runs[].id' 2>/dev/null | grep -v "^${GITHUB_RUN_ID:-none}$" | head -n 6)

for candidate in ${candidates}; do
  rm -rf "${dest}"
  if gh run download "${candidate}" --repo "${repo}" -n test-ledger-run -D "${dest}" >/dev/null 2>&1 \
     && find "${dest}" -name '*.durations.json' -type f 2>/dev/null | head -1 | grep -q .; then
    echo "duration baseline: run ${candidate}, $(find "${dest}" -name '*.durations.json' | wc -l | tr -d ' ') job file(s)"
    exit 0
  fi
done

rm -rf "${dest}"
echo "duration baseline: none found in the last few completed main runs — lanes will balance by count"
exit 0
