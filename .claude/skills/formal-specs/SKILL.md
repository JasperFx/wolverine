---
name: formal-specs
description: Run, iterate on, and extend the P model-checker specs under formal/ (leader election, agent ownership). Use when compiling or checking a .p spec, adding a new spec or test case, debugging a counterexample, running the mutant ledger, or when a change touches NodeAgentController / the assignment plane and the model should move with it.
---

# Running the P specs under `formal/`

The specs live in `formal/<topic>/` (`leader-election/`, `agent-assignment/`). Each is a
`*Model.p` / `*Spec.p` / `*Test.p` triple plus a README with a results table and a **mutant
ledger**. See `formal/README.md` for the why; this skill is the how.

## Toolchain: always use the nix shell

`p` (the model checker) and the .NET 8/9/10 SDKs come from the repo flake. Either enter the
shell (`nix develop`) or prefix each command with `nix develop <repo-root> -c ...`. `p` is a
net8.0 tool; outside the nix shell it will fail to find a .NET 8 runtime.

The generated `PChecker/`, `PGenerated/`, `PEx/`, `PCheckerOutput/` dirs are gitignored;
`formal/Directory.Build.props` / `.targets` are MSBuild barriers that keep the generated
net8.0 projects from inheriting the repo's net9/net10 + central-package config. Do not delete
them.

## Compile, then check

From inside `formal/<topic>/`:

```
p compile --pfiles <A>.p <B>.p <C>.p --projname <Name> --outdir .
p check -tc <testcase> -s 5000                # default random bugfinder
p compile ... --mode pex                      # (re)compile the PEx backend (Java/Maven)
p check --mode pex -tc <testcase> -s 1000000  # systematic exploration
```

## The flag that will bite you: `-s`, not `-i`

Use **`-s N`** (`--schedules`) for the number of schedules. Do **not** pass `-i N` — it is
not the schedules flag, and a large `-i` makes runs truncate executions mid-settling, which
shows up as **false liveness "bugs" on a correct model** (the monitor is caught in its hot
state only because the schedule was cut off). Every confusing "the pristine model fails too"
result in this repo's history traced back to `-i`. When a spec passes PEx but "fails"
default mode, suspect the flag first.

## Two modes for the scheduler

Neither is strictly better; they explore differently and catch different bugs. Run both.

- **Default (`-s`)** — random bugfinder. Samples the schedule space broadly, and can run for
  as long as you give it (the TLA+/TLC "hand it to a server for days" model). This is the
  right tool for a **large** state space that can't be closed — random sampling gets more
  breadth per unit time than a systematic search that pours its budget into one region.
  Safety counterexamples come with a readable trace.
- **PEx (`--mode pex`)** — systematic. Reports `correct for any depth` when it closes the
  space (a genuine proof for that configuration), otherwise `partially correct with N choices
  remaining` (a deep bounded search). Best when the space is small enough to close or nearly
  so — which the specs here are. PEx does **not** flag hot-state liveness the way default mode
  does, so design properties as *safety at quiescence* (assert only when nothing is in
  flight) rather than leaning on liveness.

Each has found a real bug the other's run of comparable effort missed: in this repo, random
default surfaced the `tcCrash` ordering bug a 500k-schedule PEx run had not, and PEx found the
`tcChaos` accounting gap that 100k random schedules had not. Rule of thumb: PEx for small
spaces and for the proof when it closes; random for large spaces and for long unattended
runs; both before you trust a result. Whichever you run, "no bug found" is only as strong as
the space actually covered — say what that was.

## Reading a counterexample

On a bug, `p check` writes to `PCheckerOutput/`:
- default mode: `BugFinding/<Name>_0_0.txt` — grep `ErrorLog|Assertion Failed`.
- PEx: `PEx/threads/*.log` — grep `Property violated`.
- Coverage: `BugFinding/<Name>.coverage.txt` — the authoritative answer to "is this path
  even reached?" Check it before trusting an injected `assert false` (see below). If a
  monitor's cold `Quiet` state only ever receives the first roster event and `Working` only
  transitions to itself, the system never settles.

## Checking a property isn't vacuous

A passing spec proves nothing until you show breaking the design breaks the model. Two tools:

1. **Coverage** — confirm the interesting state is reached (e.g. the duplicate-healer branch,
   the partitioned state). A "0 bugs" on an unreached branch is meaningless.
2. **Mutants** — apply one change that should break a property, re-check, confirm the
   expected counterexample. The READMEs list the committed ledger. Prefer a small Python
   file that does a literal `str.replace` on a saved pristine copy, then **verify the
   replacement applied and the file still compiles** before checking — inline
   `python3 -c` with nested quotes in a bash heredoc silently no-ops and gives false
   "mutant passes" results. Also: `var` declarations must precede statements in a P function,
   so an injected `assert` at the top of a function body is a compile error, not a check.

## Gotchas learned the hard way

- **Single-field named tuples need a trailing comma**: `(count = n,)`, `(id = id,)`.
- **Reserved words** can't be tuple fields — `on` is taken (use `run`, etc.).
- **Machine creation order ≠ id-assignment order** if ids are handed out by a server
  processing racing join messages. Assign each machine its id at construction and have it
  self-report, so a test can name a node and its id consistently.
- **FIFO mailboxes front-load pre-injected faults**: a fault message queued at t=0 is
  consumed before the later message that would make the target interesting (e.g. a node
  partitions before it ever starts running an agent). Inject faults on the *current* actor
  (an arm-then-fire-on-event pattern) rather than a fixed target up front.
- **Announce the keep-busy signal before the state that looks settled.** When a machine
  transitions into a state that would drop the system to a quiescent-looking point (a node
  dying to zero runners, a refill bumping a budget), announce the pending-fault / busy delta
  to the monitor *first*, then announce the state change. Both specs have a comment marking
  where this ordering is load-bearing.
- **Model "the loop runs forever" explicitly.** Real health-check loops poll forever; a
  finite per-run tick budget lets a node fall quiet mid-reconciliation and the monitor reads
  a false quiescence. Refill budgets on every fault and on every durable state change.

## When to touch these specs

If a change lands in `NodeAgentController` (election, heartbeat, stale-eject, step-down) or on
the assignment plane (`AssignmentGrid`, `EvaluateAssignments`, the duplicate healer,
resurrection), update the corresponding model — or, if it's out of the model's stated scope,
say so in that README's "left out" / honesty notes. A green model that no longer matches the
code is worse than no model.
