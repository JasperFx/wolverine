# Leader election, model checked

How a Balanced-mode Wolverine cluster elects and keeps exactly one leader
(`NodeAgentController` and its `HeartBeat` partial, `src/Wolverine/Runtime/Agents/`),
checked with [P](https://p-org.github.io/P/). See [`../README.md`](../README.md) for why
these specs exist and how to run them in general.

The election is a pure advisory-lock race. Every node's health-check tick 
calls `TryAttainLeadershipLockAsync`, steps down
when the lock is gone server-side, ejects sustained-stale peers, and resurrects its own
row if a peer deleted it. Because everything routes through the shared database, the whole
protocol is **CP**: a node that can reach the database participates, and a node that
cannot stops leading and stops processing (see [Network partitions](#network-partitions)
below). Leadership changing hands through crashes, deploys, dropped database sessions, and
partitions is exactly the kind of thing distributed systems trip on, so the two properties
worth stating are:

- **Convergence.** Whenever the cluster goes quiet — no tick mid-flight, no fault still
  taking effect — exactly one live node believes it is the leader, the advisory lock
  belongs to that node, its row alone carries the `wolverine://leader` assignment, every
  live node has a node row, and no stale or orphaned row survives.
- **Quiescence.** The cluster does go quiet. The hot state is what fails if the election
  stalls (a lock wedged under a dead session) or nodes trade leadership forever.

Both live in one monitor (`LeaderElectionSpec.p`), read off the same fact: whether
anything — a tick, a fault, a refill of tick budget — is still carrying work.
Convergence is asserted at every quiescent moment, not only the final one, so a run that
settles, is perturbed, and settles again is checked at both plateaus.

## What is modelled

| Model | Real thing |
| --- | --- |
| `Store` | the `wolverine_nodes` table, the `LeaderUri` assignment row, and the session-scoped advisory lock, serialized the way the database serializes them |
| `Node` | one process: `DoHealthChecksInternalAsync` as a sequence of storage round trips, plus the local `IsLeader` / `_locks` beliefs |
| `SessionReaper` | the database noticing an advisory-lock session died (process exit, network drop, idle cull) — Postgres frees session-level locks the moment the backend session ends |
| `StaleReaper` | a row's heartbeat aging past `StaleNodeTimeout` in the eyes of every observer |
| `eRefill` | the heartbeat/health-check loops running forever: every fault refunds each node a fresh budget of ticks, so quiescence is only reached after the last fault is fully absorbed |

The couriers are load-bearing: a fault's effect lands at a moment of the scheduler's
choosing, so a leader's lock session can die before, during, or long after an election,
and the lock-freed and row-went-stale effects of one crash can land in either order.

Kept faithfully, because the properties turn on them:

- The tick order of `DoHealthChecksInternalAsync`: heartbeat upsert (with the GH-3604/D2
  resurrection of a row a peer deleted, leader mark included), stale-peer ejection, the
  GH-2602 step-down when the lock is gone server-side, then always attain-or-renew.
- The `HasLock` liveness ping (GH-2602): the client's lock belief is re-derived from the
  server, never trusted on its own.
- The idempotent re-grant to the current holder — the client-side short-circuit that
  keeps Postgres session locks from stacking.
- Ejection hysteresis (`StaleNodeEjectionThreshold`, default 2): only a peer stale on
  consecutive observations is ejected, and a streak resets the moment the peer reads
  fresh. Never self (GH-1116 / GH-2682).
- Leader protection: a stale row carrying the leader mark may only be destroyed by the
  node currently holding the lock.
- `DeleteAsync` does not touch the advisory lock — ejecting a dead leader's row does not
  free the lock its dead session still holds.
- `tryStartLeadershipAsync` persists the `LeaderUri` assignment on a pooled connection,
  not the lock session — so a claim can land after the lock has already moved on, and 
  two nodes can transiently both believe they lead. The monitor tolerates the window
  (GH-2602 shrank it; nothing can remove it) and checks the outcome at quiescence.
- Graceful `StopAsync`: release the lock only if the node believes it holds it, delete
  own row — plus the process exit closing the lock session, which is the second layer
  that frees the lock when the belief was stale.

Left out: agent assignment and distribution (`EvaluateAssignmentsAsync`, the
`AssignmentGrid`, capabilities, restrictions) — this model stops at "who leads and what
the node table says", the plane the distribution sits on. Also: the separate heartbeat
loop (GH-3604/D1) — heartbeats ride the tick, and a starved heartbeat is modelled as the
`StaleReaper` blip instead; lease-based lock backends (RavenDb, CosmosDb) with their
renewal/expiry semantics — this is the Postgres/SqlServer session-lock shape; node
number vs node id (one identity here, so GH-1116 and GH-2682 collapse into one
self-check); and storage write failures. One compression: the heartbeat write, the node
state read, and the `HasLock` ping are one round trip here rather than three — all three
happen back-to-back at the top of the real tick, and the model keeps the gap that
matters, the one between reading the state and acting on it.

## Network partitions

Everything routes through the shared database, so the database *is* the arbiter — there is
no node-to-node consensus to partition. That makes the behavior CP, and it falls out of
the same mechanics the model already exercises rather than needing a case of its own.

Split the cluster into a side that can reach the database and a side that cannot:

- **The database side** keeps a quorum-of-one: the database. Nodes there go on
  heartbeating, and if the old leader is stranded on the far side, its advisory lock is
  freed the moment its session drops (session-scoped locks die with the connection), so a
  database-side node attains the freed lock and becomes the new leader. From this side, a
  stranded peer is indistinguishable from a crashed one — its heartbeat ages out and its
  row is ejected under the usual hysteresis and leader protection, and its agents are
  redistributed. This is `tcLockSessionDrop` (the lock session dies) and `tcHeartbeatBlip`
  (the row goes stale) happening together, which is exactly what `tcChaos` covers.
- **The stranded side** cannot write its heartbeat or renew its lock. `HasLeadershipLock`
  fails its liveness ping, so a stranded leader steps down (GH-2602), and no node there
  can attain the lock. It elects no one and does no leader/assignment work; its durability
  and subscription agents cannot reach their stores either. It sacrifices availability to
  preserve the single-leader invariant — the CP choice.

So the model's two faults are the two halves of a partition seen from the two sides, and
the safety property — never two leaders believing it at once, at quiescence — is precisely
the guarantee a partition would threaten in an AP design. What the model does **not**
cover is the stranded side's own liveness (it deliberately has no leader while isolated)
or the moment of *healing*, when the stranded node rejoins and its restored agent
assignments collide with what the new leader has already placed. That contention is the
subject of the sibling [`agent-assignment/`](../agent-assignment/) spec, which models the
GH-2602 duplicate healer directly.

## Running it

From the repo root, `nix develop` provides `dotnet` and `p` (see `nix/p.nix`). Then:

```
cd formal/leader-election
p compile --pfiles LeaderElectionModel.p LeaderElectionSpec.p LeaderElectionTest.p --projname LeaderElection --outdir .
p check -tc tcChaos -s 100000
```

The test cases: `tcQuietCluster` (three nodes, nothing goes wrong), `tcCrash` (a
scheduler-chosen node dies dirty — sometimes the leader, sometimes not), `tcGracefulStop`
(the deploy case), `tcLockSessionDrop` (the GH-2602 case: the lock session dies under a
live, healthy leader), `tcHeartbeatBlip` (the GH-3604 case: a live node goes stale in its
peers' eyes and may be ejected), and `tcChaos` (all of the above in one run). A network
partition is `tcLockSessionDrop` and `tcHeartbeatBlip` at once, and `tcChaos` covers that
combination — see [Network partitions](#network-partitions).

PEx explores systematically and reports `correct for any depth` when it closes the space —
worth running here, since this state space is small enough that several cases close. The
default random bugfinder is complementary, not inferior (see [`../README.md`](../README.md)
on when each wins); run both. Use `-s`, not `-i`.

```
p compile --pfiles LeaderElectionModel.p LeaderElectionSpec.p LeaderElectionTest.p --projname LeaderElection --outdir . --mode pex
p check --mode pex -tc tcChaos -s 1000000
```

## Results

PEx, 2M schedules per case (except where the space closed sooner):

| Case | Result |
| --- | --- |
| `tcQuietCluster` | **correct for any depth** (space closed, 66k schedules) |
| `tcLockSessionDrop` | **correct for any depth** (space closed, 1.25M schedules) |
| `tcCrash` | no violation, 2M schedules (partial: ~90k choices remaining) |
| `tcGracefulStop` | no violation, 2M schedules (partial: ~9k choices remaining) |
| `tcHeartbeatBlip` | no violation, 2M schedules (partial: ~36k choices remaining) |
| `tcChaos` | no violation, 2M schedules (partial: ~64k choices remaining) |

The first two are proofs for these configurations; the other four are deep bounded
searches that had not exhausted the space at 2M. Under the random bugfinder, 100k
schedules per case also found nothing — but that is the weaker statement, and it was the
random pass that lulled: PEx found a real gap in `tcChaos` at ~2k schedules that 100k
random schedules had missed (see the last mutant below).

## What the mutants say

A model that passes proves nothing until breaking the design breaks the model. Each of
these is the committed model with one thing removed:

- **The GH-2602 liveness ping** — the client-side short-circuit trusts `_locks` without
  asking the server, so a leader whose lock session died never finds out — **violated**:
  `settled with 2 live nodes believing they are the leader`, found within the first ten
  schedules of `tcLockSessionDrop`. This is the exact two-simultaneous-leaders bug
  GH-2602 fixed, and it needs nothing more exotic than one dropped connection.
- **The GH-3604/D2 resurrection** — a heartbeat miss is silently swallowed instead of
  re-registering the node's real identity — **violated**: `live node 2 has no row at
  quiescence — ejected and never resurrected`, via a heartbeat blip and a peer's
  ejection. Without the resurrection a live node deleted out from under itself is
  permanently invisible to the cluster.
- **Step-down leaves the `LeaderUri` row behind** — the lock is released but the local
  leader agent is not stopped — **violated**: `node 1's row still carries the leader
  mark while node 2 is the leader`. Anything reading leadership off the assignment
  table (`WolverineNode.IsLeader()`, which leader protection itself relies on) now sees
  two leaders.
- **The database never frees a dead session's lock** — an environment mutant, not a code
  mutant — **violated**: `settled with 0 live nodes believing they are the leader`. This
  is what the election's liveness actually rests on: session-scoped locks dying with
  their session. It is also why `HasLeadershipLock` must ping — the flip side of the
  same coin.
- **Broken quiescence accounting** (a refill swallowed without being counted) —
  **liveness violation**: `detected liveness bug in hot state 'Working' at the end of
  program execution`. Included as the proof that the hot state is not decorative: a run
  that never settles is reported even when no assert ever fires.
- **Leader protection removed** — any observer may destroy the leader's row — **not
  falsified** in 30k schedules per case. In this model the wound self-heals: the
  resurrection restores the row and the mark, and the next tick reconciles. **This is
  not a licence to delete the check** — the real damage is on the assignment plane the
  model deliberately leaves out: the ejected leader's agent assignments are wiped with
  its row, and the GH-3604 report is precisely that churn livelock. The model cannot
  see that job at all.
- **Step-down keeps the advisory lock** — **not falsified**: the ex-leader still holds
  the lock, so the next tick re-grants it and the node re-elects itself; the cluster
  converges on the same node. The release matters for handing leadership *away*
  (graceful rebalance, lease backends), not for the safety this monitor states.
- **Hysteresis removed** (eject on the first stale sighting) — **not falsified**, same
  shape: a premature eject of a live node is healed by resurrection. The threshold
  exists to avoid the eject/resurrect churn and the destroyed assignments, both outside
  this model's scope.

## What PEx caught that random sampling didn't

The mutants above are deliberate wounds. This one was real. An earlier cut of the model
passed the random bugfinder at 100,000 schedules on every case, and PEx then falsified
`tcChaos` at ~2,000 schedules: `a stale row for node 1 survived quiescence`. The defect
was in the model's own bookkeeping, not the protocol — when a late fault refilled a
node's tick budget, `consumeRefill` bumped the budget but did not re-announce the node as
busy before signalling the refill consumed, so for one instant the monitor could read the
cluster as quiescent while a just-marked stale row was still unreconciled. The fix was to
announce the refreshed busy state first (`consumeRefill` in `LeaderElectionModel.p`). It is
the case for keeping PEx in the mix: the bug was a specific interleaving where the crash's
stale-mark was delivered dead last, and random sampling sailed past it a hundred thousand
times. (The converse also happened — random default caught `agent-assignment`'s `tcCrash`
ordering bug a 500k PEx run had not — which is why the guidance is to run both.)

One honest gap: GH-2682 ("never consider self stale on this tick") cannot be exercised
here. The protection guards against a *lagged read* — snapshot isolation or replica lag
showing your own row stale right after you wrote your heartbeat — and this model's
combined write-and-read round trip has no lag by construction. A model that splits the
write from the read and lets the read return stale data would be the way to check it.
