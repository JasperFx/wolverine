#!/usr/bin/env bash
# GH-5150: net10.0 vs net11.0 vs net11.0 + runtime-async, interleaved, on the broker-free "inproc" lane.
#
#   ./runtime-async.sh [rounds] [ENV=VAL...]
#
# Builds three variants of the rig, each into its own artifacts path so no variant can pick up another's
# assemblies (the runtime-async compiler switch does not trigger a recompile on its own):
#
#   net10     net10.0, ordinary async state machines
#   net11     net11.0, ordinary async state machines
#   net11-ra  net11.0, Wolverine (and the rig) compiled with -p:WolverineRuntimeAsync=true, and the
#             runtime-compiled handlers generated with CodeGeneration.UseRuntimeAsync (RIG_RUNTIME_ASYNC=1)
#
# then runs them round-robin (rotating the starting variant each round) so drift on a shared box lands on
# every variant equally. Every run proves its IL before it measures (RIG_EXPECT_RUNTIME_ASYNC); a variant
# whose IL disagrees with its label exits 3 and the script stops.
#
# Knobs: RIG_WARMUP_S (default 10), RIG_DURATION_S (default 30), RIG_INPROC_CONCURRENCY,
# RIG_INPROC_YIELD_EVERY, RA_SKIP_BUILD=1 to reuse the last builds. Results land in
# rig-results/runtime-async-<timestamp>/ with one summary json per run and results.tsv.

set -euo pipefail
cd "$(dirname "$0")"

ROUNDS="${1:-5}"
shift || true
for assignment in "$@"; do
  export "${assignment?}"
done

export RIG_WARMUP_S="${RIG_WARMUP_S:-10}"
export RIG_DURATION_S="${RIG_DURATION_S:-30}"
export MSBUILDDISABLENODEREUSE=1

ART="$(pwd)/.runtime-async-artifacts"
OUT="$(pwd)/rig-results/runtime-async-$(date +%Y%m%d-%H%M%S)"
mkdir -p "$OUT"

VARIANTS=(net10 net11 net11-ra)

framework_of() { [[ "$1" == net10 ]] && echo net10.0 || echo net11.0; }
binary_of() { echo "$ART/$1/bin/KafkaPerfRig/release_$(framework_of "$1")/KafkaPerfRig"; }

if [[ "${RA_SKIP_BUILD:-0}" != 1 ]]; then
  for v in "${VARIANTS[@]}"; do
    extra=()
    [[ "$v" == net11-ra ]] && extra=(-p:WolverineRuntimeAsync=true)
    echo "[runtime-async.sh] building $v"
    rm -rf "${ART:?}/$v"
    dotnet build KafkaPerfRig.csproj -c Release -f "$(framework_of "$v")" --artifacts-path "$ART/$v" \
      --nologo -v q ${extra[@]+"${extra[@]}"}
  done
fi

printf "round\tvariant\tmsgPerSec\tcpuUsPerMsg\tallocBytesPerMsg\tgen0\tgen1\tgen2\tp50us\tp95us\tp99us\n" >"$OUT/results.tsv"

for ((round = 1; round <= ROUNDS; round++)); do
  for ((k = 0; k < ${#VARIANTS[@]}; k++)); do
    v="${VARIANTS[$(((k + round - 1) % ${#VARIANTS[@]}))]}"
    run="$OUT/r${round}-${v}"
    mkdir -p "$run"
    ra=0
    [[ "$v" == net11-ra ]] && ra=1
    echo "[runtime-async.sh] round $round: $v"
    RIG_VARIANT="$v" RIG_RUNTIME_ASYNC="$ra" RIG_EXPECT_RUNTIME_ASYNC="$ra" RIG_OUT="$run" \
      "$(binary_of "$v")" inproc >"$run/inproc.log" 2>&1 || {
        echo "[runtime-async.sh] $v failed; see $run/inproc.log"
        tail -20 "$run/inproc.log"
        exit 1
      }
    python3 -I - "$run/inproc-summary.json" "$round" >>"$OUT/results.tsv" <<'PY'
import json, sys
s = json.load(open(sys.argv[1]))
keys = ["variant", "msgPerSec", "cpuUsPerMsg", "allocBytesPerMsg", "gen0", "gen1", "gen2", "p50us", "p95us", "p99us"]
print("\t".join([sys.argv[2]] + [str(s[k]) for k in keys]))
PY
  done
done

echo "[runtime-async.sh] medians:"
python3 -I - "$OUT/results.tsv" <<'PY' | tee "$OUT/medians.txt"
import csv, statistics, sys
rows = list(csv.DictReader(open(sys.argv[1]), delimiter="\t"))
cols = ["msgPerSec", "cpuUsPerMsg", "allocBytesPerMsg", "p50us", "p99us"]
print("variant\t" + "\t".join(cols))
base = {}
for v in ["net10", "net11", "net11-ra"]:
    sel = [r for r in rows if r["variant"] == v]
    med = {c: statistics.median(float(r[c]) for r in sel) for c in cols}
    base.setdefault("net10", med)
    print(v + "\t" + "\t".join(
        f"{med[c]:.3f} ({(med[c] / base['net10'][c] - 1) * 100:+.1f}%)" if base["net10"][c] else f"{med[c]:.3f}"
        for c in cols))
PY
echo "[runtime-async.sh] results in $OUT"
