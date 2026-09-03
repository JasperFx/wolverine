# Formal specs

Model-checked specifications of Wolverine's trickier distributed protocols, written in
[P](https://p-org.github.io/P/). These are not tests of the C# — they are executable
models of the *design*, checked exhaustively against safety and liveness properties that
ordinary unit and integration tests cannot reach: the properties that only break under a
specific interleaving of crashes, dropped connections, and racing timers.

A distributed protocol has too many interleavings to enumerate by hand or to hit
reliably with a test that runs the real cluster a few hundred times. A model checker
explores them systematically. The point is not to prove the C# correct — the model is an
abstraction, and the README in each spec directory is explicit about what it abstracts
away — but to prove the *design* sound, to turn "we're pretty sure leadership can't get
stuck" into a counterexample or a closed search, and to keep a written, checkable record
of why each guard in the code is load-bearing.

## The specs

| Directory | Models |
| --- | --- |
| [`leader-election/`](leader-election/) | `NodeAgentController`'s advisory-lock leader election — crashes, graceful stops, dropped lock sessions, heartbeat blips, and network partitions — converging to exactly one leader |
| [`agent-assignment/`](agent-assignment/) | single-agent ownership across a partition heal — the assignment plane: a cut-off node keeps running its agent while the leader places a copy elsewhere, and the GH-2602 duplicate healer must reconverge to exactly one runner |

## Toolchain

P ships as a .NET tool and needs a .NET 8 SDK to build the projects it generates; its
systematic PEx backend generates and builds Java. The repo's Nix flake provides all of
it. From the repo root:

```
nix develop          # provides `p`, the .NET 8/9/10 SDKs, and a JDK + Maven for PEx
cd formal/<spec>
p compile --pfiles *.p --projname <Name> --outdir .
p check --mode pex -tc <testcase> -s 1000000
```

Use **`-s N`** (`--schedules`) for the schedule count, **not `-i N`** — a misused `-i`
truncates executions mid-settle and reports false liveness "bugs" on a correct model. The
`formal-specs` skill (`.claude/skills/formal-specs/`) collects this and the other P gotchas.

Each spec's own README has its exact commands, its results, and — importantly — its
**mutant ledger**: a passing model proves nothing until deliberately breaking the design
is shown to break the model, so every spec records which mutations it catches and, for
the ones it doesn't, an honest account of which layer actually absorbs them.

## Two modes — complementary

`p check` defaults to a random-sampling bugfinder; `--mode pex` explores systematically and
reports `correct for any depth` when it closes the space (a proof for that configuration).
Neither dominates. PEx is the better choice when the space is small enough to close or nearly
so — which these specs are. But a large space that can't be closed is exactly where random
sampling earns its keep: it covers more breadth per unit time than a systematic search that
spends its budget deep in one region, and it can run unattended for as long as you give it
(the way a TLA+/TLC model gets handed to a server for days). Each has caught a real bug the
other missed here — random found `agent-assignment`'s `tcCrash` ordering bug a 500k-schedule
PEx run had not; PEx found `leader-election`'s `tcChaos` accounting gap that 100k random
schedules had not — so run both, and remember "no bug found" is only as strong as the space
actually covered.

## Build isolation

`formal/Directory.Build.props` and `Directory.Build.targets` are deliberate MSBuild
barriers. The generated P checker projects target net8.0, and without these the upward
MSBuild search would hand them the repo root's `Directory.Build.props` (which pins
`net9.0;net10.0` and central package management) and break their restore. Leave them in
place. The generated `PChecker/`, `PGenerated/`, `PEx/`, and `PCheckerOutput/` directories
are gitignored — the `.p` sources are the only thing worth keeping.
