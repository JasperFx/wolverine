#!/usr/bin/env bash
#
# Samples memory pressure to stdout while a CI test target runs, and -- when the target stops
# making progress -- captures the async stacks of the wedged process before the 20 minute job cap
# kills the runner and takes the evidence with it.
#
# Why stdout and not a file: the failures this exists to diagnose are ones where every later step
# is skipped -- the runner killed outright (GH-3771), or the job cancelled at the workflow's
# timeout-minutes cap (GH-4083) -- so an artifact upload or an `if: failure()` dmesg dump never
# executes. Anything already streamed to the live job log, on the other hand, survives. So this has
# to run in the background of the step being diagnosed and print as it goes, and nothing here may
# use ::group:: -- a group left unclosed by a cancellation hides everything inside it.
#
# Each line is prefixed [mem] or [stall] so the curve and the diagnostic can be pulled out of a
# noisy log with a single grep:
#
#   gh run view <id> --log | grep -E '\[mem\]|\[stall\]'
#
# THE MEMORY CURVE reads as: if MemAvailable trends toward zero and swap fills just before the
# kill, this is the OOM killer taking the runner service and the fix is footprint (lane count,
# per-lane hosts). If memory is flat at the moment of death, it is not OOM and lane tuning would be
# treating the wrong thing.
#
# THE STALL WATCHDOG exists because "memory is flat" turned out to be the answer, and on its own it
# is not actionable. On the GH-4083 occurrence the samples were byte-identical for the last several
# minutes -- 13.7GB available, no swap, the test host pinned at 1160MB -- which says the process was
# idle, not starved, but says nothing about WHICH test was wedged. Bobcat 0.6.1 reports per-test
# results only when a batch finishes, so a batch that never finishes prints nothing at all: the last
# line of that job was "275 test(s): 275 batched, 0 isolated", 18m33s before the cap. The watchdog
# closes that gap by dumping the async stacks itself. Per the repo's convention, that means
# dotnet-dump + `dumpasync`, NOT dotnet-stack: the wedge is an await living on the GC heap, and a
# thread report just shows pool workers parked on a semaphore.
#
# Sizing, from 116 green Marten shard jobs: avg 380s, worst 476s. The stall threshold and deadline
# below sit well clear of that and well clear of the 20 minute cap, so a healthy run can never pay
# for the diagnostic and a wedged one always gets it with time to spare.

set -uo pipefail

interval="${MEMORY_SAMPLE_INTERVAL:-15}"

# Consecutive seconds of a genuinely idle watched process before the watchdog fires.
stall_after="${STALL_AFTER_SECONDS:-300}"

# Don't watch for stalls until the build/restore phase is behind us. VBCSCompiler is the largest
# process on the box while a project compiles and idles between compilations, which is not a stall.
stall_arm_after="${STALL_ARM_AFTER_SECONDS:-240}"

# Unconditional backstop: fire this many seconds in whatever the CPU says, because a wedge with a
# live poller in it (a durability agent, a Marten daemon) burns enough CPU to never look idle. Set
# below the workflow's timeout-minutes cap with room for the capture itself to finish and print.
stall_deadline="${STALL_DEADLINE_SECONDS:-780}"

# A watched process using less than this share of one core across a whole interval is idle.
stall_cpu_percent="${STALL_CPU_PERCENT:-2}"

# The async stack of a wedged test host is worth having; all 40,000 lines of it are not.
stall_dump_lines="${STALL_DUMP_LINES:-400}"

clock_tick="$(getconf CLK_TCK 2>/dev/null || echo 100)"
idle_jiffy_floor=$(( clock_tick * interval * stall_cpu_percent / 100 ))

started="${SECONDS}"
fired=0
prev_pid=""
prev_rss=""
prev_cpu=""
idle_for=0

echo "[mem] sampling every ${interval}s -- total/used/free/available in MB, plus the largest RSS consumers"
echo "[stall] watchdog armed at +${stall_arm_after}s: dumps async stacks after ${stall_after}s idle, or at +${stall_deadline}s regardless"

# Everything the kernel will tell us for free, printed before anything slow is attempted so that a
# capture truncated by the cap still leaves the cheap half behind.
cheap_evidence() {
    local pid="$1"

    echo "[stall] --- process tree ---"
    ps -eo pid,ppid,stat,wchan:24,etime,time,rss,comm --sort=-rss | head -15 | sed 's/^/[stall] /'

    if [ -r "/proc/${pid}/status" ]; then
        echo "[stall] --- /proc/${pid} ---"
        grep -E '^(Name|State|Threads|VmRSS|voluntary_ctxt_switches|nonvoluntary_ctxt_switches):' \
            "/proc/${pid}/status" 2>/dev/null | sed 's/^/[stall] /'
        echo "[stall] open fds: $(ls "/proc/${pid}/fd" 2>/dev/null | wc -l)"

        # A thread histogram separates "every thread parked" (the await wedge this is looking for)
        # from "one thread spinning" (a livelock, which needs a different tool).
        echo -n "[stall] thread states: "
        for t in "/proc/${pid}"/task/*/stat; do
            rest="$(cat "$t" 2>/dev/null)"; rest="${rest#*) }"
            echo "${rest%% *}"
        done | sort | uniq -c | tr '\n' ' '
        echo
    fi

    # Which sockets it is holding is often the whole answer: a test host parked on a connection to
    # Postgres is a very different bug from one parked on nothing at all.
    echo "[stall] --- sockets ---"
    ss -tnp 2>/dev/null | grep -E "pid=${pid}[,)]" | head -20 | sed 's/^/[stall] /' \
        || echo "[stall] (no sockets attributed to ${pid})"

    echo "[stall] --- memory ---"
    free -m | sed 's/^/[stall] /'
}

# dotnet-dump + dumpasync. Installed lazily: a healthy run must not pay ~20s to install a tool it
# will never use. Every stage is bounded by `timeout` so a capture that goes wrong cannot itself be
# the thing that eats the remaining budget.
async_stacks() {
    local pid="$1"
    local dump="${RUNNER_TEMP:-/tmp}/stall-${pid}.dmp"

    export PATH="${PATH}:${HOME}/.dotnet/tools"
    if ! command -v dotnet-dump >/dev/null 2>&1; then
        echo "[stall] installing dotnet-dump..."
        timeout 120 dotnet tool install -g dotnet-dump >/dev/null 2>&1 \
            || { echo "[stall] dotnet-dump install FAILED -- cheap evidence above is all there is"; return 1; }
    fi

    echo "[stall] collecting a dump of pid ${pid}..."
    timeout 240 dotnet-dump collect -p "${pid}" -o "${dump}" >/dev/null 2>&1 \
        || { echo "[stall] dotnet-dump collect FAILED"; rm -f "${dump}"; return 1; }

    echo "[stall] --- dumpasync --coalesce (first ${stall_dump_lines} lines) ---"
    # Long-form flag only: `printf` with the short forms gets mangled into "Unrecognized argument".
    printf 'dumpasync --coalesce\nexit\n' \
        | timeout 300 dotnet-dump analyze "${dump}" 2>&1 \
        | head -n "${stall_dump_lines}" \
        || true
    echo "[stall] --- end dumpasync ---"

    rm -f "${dump}"
}

fire() {
    local pid="$1" why="$2"
    fired=1

    echo "[stall] ================================================================"
    echo "[stall] NO PROGRESS: ${why} (pid ${pid}, $(( SECONDS - started ))s into this step)"
    echo "[stall] ================================================================"

    cheap_evidence "${pid}"
    async_stacks "${pid}"

    echo "[stall] capture complete; sampling continues"
}

while true; do
    # `free -m` line 2 is the physical memory row; column 7 is "available", which is the number that
    # actually predicts an OOM kill (unlike "free", which excludes reclaimable page cache).
    read -r _ total used free _ _ available <<<"$(free -m | awk 'NR==2')"
    swap_used="$(free -m | awk 'NR==3 {print $3}')"

    # Top three processes by RSS, so a spike can be attributed to a test host / Postgres / the runner
    # rather than just observed in aggregate.
    top="$(ps -eo rss=,comm= --sort=-rss | head -3 | awk '{printf "%s=%dMB ", $2, $1/1024}')"

    # The largest process is the one worth watching: on every Marten shard that is the test host
    # from the moment it starts, and a wedge is by definition something that has already started.
    read -r pid rss _ <<<"$(ps -eo pid=,rss=,comm= --sort=-rss | head -1)"

    cpu=""
    # The pid guard is not paranoia: "/proc/${pid}/stat" with an empty pid collapses to the
    # system-wide /proc/stat, whose twelfth field is the string "cpu0" -- which bash then evaluates
    # as an unbound variable and kills the sampler outright.
    if [[ "${pid}" =~ ^[0-9]+$ ]] && [ -r "/proc/${pid}/stat" ]; then
        # comm can contain spaces and parens, so cut everything through ") " before splitting;
        # what remains starts at field 3 (state), putting utime/stime at offsets 12 and 13.
        stat_rest="$(cat "/proc/${pid}/stat" 2>/dev/null)"; stat_rest="${stat_rest#*) }"
        # shellcheck disable=SC2086
        set -- ${stat_rest}
        cpu=$(( ${12:-0} + ${13:-0} ))
    fi

    cpu_delta="n/a"
    if [ -n "${cpu}" ] && [ "${pid}" = "${prev_pid}" ] && [ -n "${prev_cpu}" ]; then
        cpu_delta=$(( cpu - prev_cpu ))
        if [ "${rss}" = "${prev_rss}" ] && [ "${cpu_delta}" -le "${idle_jiffy_floor}" ]; then
            idle_for=$(( idle_for + interval ))
        else
            idle_for=0
        fi
    else
        idle_for=0
    fi

    echo "[mem] $(date -u +%H:%M:%S) total=${total} used=${used} free=${free} available=${available} swap_used=${swap_used} | ${top}| watching pid ${pid} cpu_jiffies+${cpu_delta} idle_for=${idle_for}s"

    if [ "${fired}" -eq 0 ] && [ $(( SECONDS - started )) -ge "${stall_arm_after}" ]; then
        if [ "${idle_for}" -ge "${stall_after}" ]; then
            fire "${pid}" "watched process idle for ${idle_for}s (RSS unchanged, under ${stall_cpu_percent}% of one core)"
        elif [ $(( SECONDS - started )) -ge "${stall_deadline}" ]; then
            fire "${pid}" "still running ${stall_deadline}s in -- the worst green Marten shard on record finished in 476s"
        fi
    fi

    prev_pid="${pid}"
    prev_rss="${rss}"
    prev_cpu="${cpu}"

    sleep "${interval}"
done
