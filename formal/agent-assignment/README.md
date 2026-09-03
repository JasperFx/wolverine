# Single-agent ownership, model checked

Does a leader-assigned singular agent end up running on exactly one live node after a
network partition heals? This is the assignment plane the [leader-election
spec](../leader-election/) deliberately leaves out, and the contention it asks about is the
GH-2602 one: a node cut off from the database keeps running its agent while the leader,
seeing it gone, places a copy elsewhere — so for the duration of the partition there are
genuinely two runners, and when the cut-off node returns the cluster has to notice and
heal the duplicate. See [`../README.md`](../README.md) for why these specs exist and how to
run them.

Two facts have to agree in the end: the **durable assignment row** in the store
(`AddAssignmentAsync` / `RemoveAssignmentAsync`) and the **node-local fact** of actually
running the agent (`Agents[uri]`). A partition splits them, and the interesting question is
whether they reconverge.

- **Convergence.** When the cluster goes quiet — no partition still in effect, no placement
  command or row write in flight — exactly one live node runs the agent, that node holds
  the one durable row, and no other row survives.
- **Quiescence.** It does go quiet: a duplicate is always healed, an unowned agent is
  always placed, and the leader doesn't churn forever.

Both are one monitor (`AgentOwnershipSpec.p`), asserted at every quiescent moment — never
mid-partition, when two runners are unavoidable and correct.

## What is modelled

| Model | Real thing |
| --- | --- |
| `Store` | the `node_assignments` rows, the live-node set, and the leader's placement, serialized the way the database serializes them |
| `Node` | one process: its health-check tick (restore my own row; if I'm the leader, evaluate placement) and the local fact of running the agent |
| `RunCourier` | an `AssignAgent` / `StopRemoteAgent` command in flight to a node |
| `Arsonist` / `StrikeCourier` | inject a fault on whoever is *currently* running the agent: the arsonist arms the store, which fires the strike (cutoff or crash) on the next node to become owner |
| `HealCourier` | the partition ending |
| `eRefill` | the health-check loop running forever: faults and durable changes refund tick budgets so the leader keeps polling until the cluster is clean |

Faults hit the current owner, not a fixed node up front, because that is the interesting
case: a fault delivered before the agent is placed just hits an idle node, and a FIFO mailbox
would order a pre-injected cutoff ahead of the start that would make the node run.

Kept faithfully, because the properties turn on them:

- A node writes its **own** durable row after it actually starts the agent
  (`StartAgentAsync → upsertAssignmentAsync`) and removes it after it stops. A start command
  lost to an unreachable node therefore writes no row — the row can never outlive its
  runner, which a leader-writes-the-row shortcut would allow.
- The **GH-3604/D2 resurrection**: a node re-adds its durable row on its next tick if the row
  is missing while it is still running the agent. This is what makes split-brain residue
  *visible* — without it a healed node runs the agent with no row and the leader never sees
  the duplicate to heal it.
- The **GH-2602 duplicate healer**: when two live nodes hold a row for the agent, the leader
  stops all but one. Modelled as keeping one deterministic survivor; correctness only needs
  the survivor to be a single live node.
- Placement of an unowned agent onto a live node, and reassignment after the owner leaves —
  the same `EvaluateAssignmentsAsync` decisions, for one agent.
- Leadership can move (the store grants it to a live node when the current leader is gone),
  so a *new* leader heals residue left around a partition — but the single-leader invariant
  itself is **assumed**, not re-derived: it is what the leader-election spec proves. This
  spec builds on that and studies what the leader does with the agent.

Left out, deliberately: multiple agents and even distribution (`DistributeEvenly`, the full
`AssignmentGrid`) — this is one singular agent; the pending-assignment ledger and command
batching (GH-3698/GH-3604/D3) — real optimisations that reduce transient duplicates, but the
healer is what makes the *property* hold, so the model lets the transient happen and heals
it; blue/green capability matching; and the advisory-lock election mechanics themselves
(the sibling spec's job). A partition here is a node excluded from the live set while it
keeps running — ejection hysteresis is folded into that, since the leader-election spec
already checks the eject path.

## Running it

From the repo root, `nix develop` provides `dotnet` and `p`. Then:

```
cd formal/agent-assignment
p compile --pfiles AgentOwnershipModel.p AgentOwnershipSpec.p AgentOwnershipTest.p --projname AgentOwnership --outdir .
p check --mode pex -tc tcChaos -s 1000000
```

The cases: `tcSteadyState` (no fault), `tcPartitionHeal` (owner cut off, then heals — the
partition-heal duplicate), `tcCrash` (owner dies permanently, forcing reassignment — which
makes the placement path load-bearing, since a heal alone lets a node resurrect its own
ownership), and `tcChaos` (both). Run both PEx and the random bugfinder — they catch
different bugs (see [`../README.md`](../README.md)) — and use `-s`, not `-i`.

## Results

PEx, 1M schedules per case (no bugs; deep bounded searches — the space did not close):

| Case | Result |
| --- | --- |
| `tcSteadyState` | no violation, 1M schedules |
| `tcPartitionHeal` | no violation, 1M schedules |
| `tcCrash` | no violation, 1M schedules |
| `tcChaos` | no violation, 1M schedules |

Coverage confirms the interesting paths are actually exercised rather than passing
vacuously: in `tcPartitionHeal` a running owner receives the cutoff, enters the partitioned
state, heals, its restored row makes the duplicate visible, and the leader emits a
`StopRemoteAgent` (the duplicate healer) — and even `tcSteadyState` exercises the healer,
because the startup assignment race can briefly place the agent on two nodes before one row
becomes visible.

The mutants below are caught as **safety** violations (an assertion at quiescence): a
persistent bad state is made to settle so the monitor sees it, rather than relying on
hot-state liveness.

## What the mutants say

Each is the committed model with one thing removed:

- **The duplicate healer** (the `owners >= 2` branch) — the leader no longer stops the extra
  copies — **violated** on `tcPartitionHeal`: `settled with 2 nodes running the single agent`.
  A partitioned owner keeps running; the leader places a copy on a peer; the original heals
  and, with nothing to heal the split, both run for good. This is the GH-2602 residue the
  `StopRemoteAgent` exists to clean up.
- **The GH-3604/D2 resurrection** — a healed node never re-adds its durable row — **violated**
  on `tcPartitionHeal`: `settled with 2 nodes running`. The subtle one: the healed node is
  still running the agent, but with no row the leader cannot *see* the duplicate, so the
  healer never fires and the two copies run on forever. The resurrection isn't only about not
  losing a node — it is what surfaces the split-brain so it can be healed.
- **Placement of an unowned agent** — the leader never assigns — **violated** on `tcCrash`:
  `settled with 0 nodes running`. When the sole owner crashes for good, nothing re-places the
  agent and it stops running anywhere. (A partition heal alone would not catch this — the
  returning node resurrects its own ownership — which is why the crash case is what makes the
  placement path load-bearing.)

Not mutated but worth stating: the model itself was wrong twice in ways the checker caught,
both fixed in the committed version. First, having the *store* write the assignment row at
dispatch (instead of the node writing its own after it starts) let a row outlive its runner
when a start was lost — PEx surfaced it as an orphan row at quiescence. Second, a node's
`Dead` entry announced the death before opening its crash fault, so the monitor saw a
momentary zero-runner "quiescence" before recovery began and fired a false alarm; opening the
fault first fixed it. Both are the same lesson the leader-election spec records: tell the
monitor about the work still owed before you announce the step that looks like rest.
