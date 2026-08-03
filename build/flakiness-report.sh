#!/usr/bin/env bash
#
# The run-level half of the retry ledger (GH-3787). Each test job writes its own ledger JSON (see
# build/RetryLedger.cs) and uploads it; this aggregates all of them into one table on the run
# summary and — the part that actually matters — diffs the run against the last `main` run that
# published a ledger.
#
# The ASB story this exists for: 22 of 25 retries burned on four consecutive green main runs. The
# absolute 22 was not the signal. The step from 1 to 22 between two adjacent runs was, and there was
# nowhere that number had ever been written down to compare against.
#
# Informational by design: this script never fails the run. A retry count is something a human
# explains, not a gate — a suite legitimately sitting at 3 would otherwise be one bad day from a red
# main.
#
# Usage: flakiness-report.sh <ledger-dir> <output-dir>
#   ledger-dir  contains the downloaded per-job artifacts (searched recursively for *.json)
#   output-dir  receives aggregate.json, uploaded as this run's baseline for the next one
#
# Expects in the environment: GH_TOKEN, GITHUB_REPOSITORY, GITHUB_RUN_ID, GITHUB_STEP_SUMMARY.

set -uo pipefail

ledger_dir="${1:?usage: flakiness-report.sh <ledger-dir> <output-dir>}"
output_dir="${2:?usage: flakiness-report.sh <ledger-dir> <output-dir>}"

repo="${GITHUB_REPOSITORY:-JasperFx/wolverine}"
summary="${GITHUB_STEP_SUMMARY:-/dev/stdout}"

mkdir -p "${output_dir}"

# ── This run ────────────────────────────────────────────────────────────────

# One object per project run. Several projects can report under one job (CIAWS, CIMQTT), so the job
# rows are a group-by rather than a rename.
ledger_files=$(find "${ledger_dir}" -name '*.json' -type f 2>/dev/null | sort)
if [ -n "${ledger_files}" ]; then
  # Ledger file names are composed from job/project/framework — no spaces to quote around.
  # shellcheck disable=SC2086
  jq -s '.' ${ledger_files} > "${output_dir}/entries.json" || echo '[]' > "${output_dir}/entries.json"
else
  echo '[]' > "${output_dir}/entries.json"
fi

# The field names here are the contract with LedgerEntry in build/RetryLedger.cs. Assert rather than
# let jq quietly resolve a renamed field to null and report a comforting zero.
if [ "$(jq 'length' "${output_dir}/entries.json")" != "0" ]; then
  if [ "$(jq '[.[] | select(.Job == null or .RetriesPerformed == null)] | length' "${output_dir}/entries.json")" != "0" ]; then
    echo "::error::Ledger entries are missing Job/RetriesPerformed — build/RetryLedger.cs and this script have drifted."
    jq '.[0]' "${output_dir}/entries.json"
    exit 0
  fi
fi

# flakyFailures carries the first-attempt failure reason alongside the name. The `// []` matters:
# ledgers written before that field existed do not have it, and the baseline is downloaded from an
# EARLIER run's artifact, so without the default one older baseline nulls the whole roll-up.
jq '[group_by(.Job)[] | {
      job:           .[0].Job,
      tests:         (map(.Tests)            | add),
      retries:       (map(.RetriesPerformed) | add),
      passedOnRetry: (map(.PassedOnRetry)    | add),
      failed:        (map(.Failed)           | add),
      indeterminate: (map(.Indeterminate)    | add),
      flakyTests:    (map(.FlakyTests[])     | unique),
      flakyFailures: (map(.FlakyFailures // [] | .[]) | unique)
    }] | sort_by(-.retries, .job)' \
  "${output_dir}/entries.json" > "${output_dir}/aggregate.json"

total_retries=$(jq '[.[] | .retries] | add // 0' "${output_dir}/aggregate.json")
total_tests=$(jq '[.[] | .tests] | add // 0' "${output_dir}/aggregate.json")
reporting_jobs=$(jq 'length' "${output_dir}/aggregate.json")

# ── The baseline ────────────────────────────────────────────────────────────
#
# The newest completed main run that actually published an aggregate. Walking a few back rather than
# taking the immediately previous one: a cancelled or infrastructure-failed run publishes nothing,
# and "no baseline" is a worse answer than "a slightly older baseline".

baseline_file=""
baseline_run=""

# An explicit baseline, for exercising the diff path outside of CI.
if [ -n "${FLAKINESS_BASELINE_FILE:-}" ] && [ -f "${FLAKINESS_BASELINE_FILE}" ]; then
  baseline_file="${FLAKINESS_BASELINE_FILE}"
  baseline_run="local"
else
  candidates=$(gh api "repos/${repo}/actions/workflows/tests.yml/runs?branch=main&status=completed&per_page=10" \
    --jq '.workflow_runs[].id' 2>/dev/null | grep -v "^${GITHUB_RUN_ID:-none}$" | head -n 6)

  for candidate in ${candidates}; do
    if gh run download "${candidate}" --repo "${repo}" -n test-ledger-run -D "${output_dir}/baseline" >/dev/null 2>&1 \
       && [ -f "${output_dir}/baseline/aggregate.json" ]; then
      baseline_file="${output_dir}/baseline/aggregate.json"
      baseline_run="${candidate}"
      break
    fi
    rm -rf "${output_dir}/baseline"
  done
fi

baseline_retries=""
if [ -n "${baseline_file}" ]; then
  baseline_retries=$(jq '[.[] | .retries] | add // 0' "${baseline_file}")
fi

# ── Jobs that reported nothing ──────────────────────────────────────────────
#
# A job killed by the 20-minute cap produces no ledger at all — Bobcat prints its summary only at the
# end, so a cap-killed job reports not even a partial count. That silence is itself a finding, and
# without this it would read as "no flakiness".

#
# Jobs that legitimately produce nothing are listed here rather than reported every run: a section
# that cries wolf on every single run is how a signal channel gets ignored, which is the failure
# this whole report exists to undo. CIAotSmoke builds and RUNS two AOT smoke apps -- it invokes no
# test project at all, so it has no retry budget to spend.
NO_LEDGER_EXPECTED="CIAotSmoke"

missing=""
# gh api prints the error BODY to stdout on a failed request, so the exit code has to gate this —
# otherwise a 404 lands in the report as the name of a job that "reported no ledger".
if gh api "repos/${repo}/actions/runs/${GITHUB_RUN_ID:-0}/jobs?per_page=100" --paginate \
     --jq '.jobs[] | select(.conclusion != "skipped" and .conclusion != null) | select(.name | startswith("CI")) | .name' \
     > "${output_dir}/jobs.txt" 2>/dev/null
then
  missing=$(sort -u "${output_dir}/jobs.txt" | while read -r job; do
    # [[ ]] rather than case: bash 3.2 (what macOS ships) mis-parses a case pattern's ")" as the
    # end of the enclosing $( ) substitution.
    if [[ " ${NO_LEDGER_EXPECTED} " == *" ${job} "* ]]; then continue; fi
    if [ "$(jq --arg j "${job}" '[.[] | select(.job == $j)] | length' "${output_dir}/aggregate.json")" = "0" ]; then
      echo "${job}"
    fi
  done)
fi

# ── The report ──────────────────────────────────────────────────────────────

delta_for() {
  local current="$1" previous="$2"
  if [ -z "${previous}" ]; then echo "—"; return; fi
  local d=$((current - previous))
  if   [ "${d}" -gt 0 ]; then echo "**+${d}**"
  elif [ "${d}" -lt 0 ]; then echo "${d}"
  else echo "·"; fi
}

{
  echo "## Flakiness roll-up"
  echo

  if [ -n "${baseline_retries}" ]; then
    if [ "${baseline_run}" = "local" ]; then
      described="a locally supplied baseline"
    else
      described="last \`main\` run [#${baseline_run}](https://github.com/${repo}/actions/runs/${baseline_run})"
    fi
    echo "**${total_retries} retries** across ${reporting_jobs} reporting job(s), ${total_tests} tests."
    echo "Baseline — ${described}: **${baseline_retries}**, $(delta_for "${total_retries}" "${baseline_retries}")."
  else
    echo "**${total_retries} retries** across ${reporting_jobs} reporting job(s), ${total_tests} tests."
    echo "No baseline found in the last few completed \`main\` runs — reporting absolute numbers only."
  fi
  echo

  echo "A retry means a test failed and then passed alone in a fresh process. Zero is the only number"
  echo "that needs no explanation; a count that *moved* is the one worth chasing."
  echo

  if [ -n "${missing}" ]; then
    # `paste -sd', '` would cycle the two delimiters and yield "a,b c,d" -- one delimiter, then sed.
    echo "> **Reported no ledger at all:** $(echo "${missing}" | paste -sd, - | sed 's/,/, /g')"
    echo ">"
    echo "> Bobcat prints its summary only at the end, so a job killed by the 20-minute cap reports"
    echo "> nothing — not even a partial count. Treat this as unmeasured, not as clean."
    echo
  fi

  if [ "${total_retries}" = "0" ] && [ "$(jq '[.[] | select(.failed > 0 or .indeterminate > 0)] | length' "${output_dir}/aggregate.json")" = "0" ]; then
    echo "No job spent a retry."
  else
    echo "| job | retries | baseline | Δ | passed on retry | failed | indeterminate |"
    echo "|---|--:|--:|:--|--:|--:|--:|"

    jq -r '.[] | select(.retries > 0 or .failed > 0 or .indeterminate > 0)
           | [.job, .retries, .passedOnRetry, .failed, .indeterminate] | @tsv' \
      "${output_dir}/aggregate.json" \
    | while IFS=$'\t' read -r job retries passed_on_retry failed indeterminate; do
        was=""
        if [ -n "${baseline_file}" ]; then
          was=$(jq -r --arg j "${job}" '[.[] | select(.job == $j) | .retries] | first // ""' "${baseline_file}")
        fi
        echo "| ${job} | ${retries} | ${was:-—} | $(delta_for "${retries}" "${was}") | ${passed_on_retry} | ${failed} | ${indeterminate} |"
      done
  fi
  echo

  if [ "${total_retries}" != "0" ]; then
    echo "<details><summary>Tests that only passed on a retry</summary>"
    echo
    # Each test is followed by why its first attempt failed, when the ledger recorded one. A name
    # alone says which test to look at; the reason is what makes it possible to act without
    # reproducing the whole job.
    jq -r '
      def reason_for($t): [.flakyFailures[]? | select(.Test == $t)] | first;
      .[] | select(.retries > 0)
      | "- **\(.job)**",
        (.flakyTests[] as $t
         | "  - `\($t)`",
           (reason_for($t)
            | select(. != null and ((.ErrorType // .Reason) != null))
            | "    - attempt \(.Attempt): \(.ErrorType // "")\(if .Reason then " — \(.Reason)" else "" end)"))' \
      "${output_dir}/aggregate.json"
    echo
    echo "</details>"
    echo
  fi
} >> "${summary}"

# Also on stdout, so the roll-up is greppable from `gh run view --log` the way the per-job lines are.
echo "=== flakiness roll-up: ${total_retries} retries, baseline ${baseline_retries:-none} (run ${baseline_run:-none}) ==="

exit 0
