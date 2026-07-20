# Execution plan — wolverine#3376 (daemon connection scoping) + #3397 Phase 2 (adaptive budget)

> **STATUS 2026-07-17 — Wave 1 COMPLETE AND MERGED to main (6.20.0-alpha.1 line).**
> - #3439 — scope tenant scheduled polling to the owning node (the #3376 fix) + tests ✅ merged
> - #3440 — release daemon tracker subscriptions on the 1→0 transition ✅ merged
> - #3441 — GH-3166 DLQ test cleans Wolverine storage (unrelated hygiene found en route) ✅ merged
> - Issue comment posted: https://github.com/JasperFx/wolverine/issues/3376#issuecomment-5002738071
>
> Root cause confirmed empirically by a two-node test, not just by reading. Waves 2 and 3 remain.
>
> Companion doc: `CONNECTION-BUDGET-3397-PLAN.md` (Phase 1, shipped in 6.19.0 via #3422).

---

## ⚠️ Corrections to the 2026-07-16 analysis below (read these first)

The mechanism was right; two supporting claims were wrong, and one of them was headed for the issue.

1. **"Single-database deployments already do this right, on the leader only" is FALSE — do not say
   this publicly.** `MessageDatabase.Agents.cs` (the `IAgentFamily` with
   `AutoStartScheduledJobPolling = true` + `RunOnLeader`) is **dead code**. Agent families come from
   `_container.GetAllInstances<IAgentFamily>()` (`WolverineRuntime.Agents.cs:241`) and the store is
   registered only as `IMessageStore`; `NodeAgentController.cs:88-91` then hands the `wolverinedb`
   scheme to `MessageStoreCollection` unconditionally. Single-DB hosts fan out on every node too —
   it just never hurt, because every node already talks to the main database for heartbeats and
   leadership. The "irony" paragraph in the draft comment must go.

2. **`MultiTenantedMessageStore.StartScheduledJobs` (the `CompositeAgent` over `AllActive()` with the
   stale TODO) is not in the path.** `MessageStoreCollection.InitializeAsync:106-114` already flattens
   every tenant database into `_services`, so `FindAllAsync()` returns them individually. Red herring.

3. **The better framing: #3376 is #2623, unfixed for relational stores.** RavenDb and CosmosDb already
   return a *non-started* agent from `StartScheduledJobs`, with a comment saying exactly why
   ("NodeAgentController owns the durability agent lifecycle... do not start a second instance here").
   The RDBMS and Oracle stores were never brought along. This is verifiable, and it's a stronger story
   than the false one.

4. **The fan-out agent is never started** — `DurableScheduledJobs` is only ever `StopAsync`'d
   (`WolverineRuntime.Disposal.cs:22`). The eagerly-started poll timer was its entire runtime
   contribution, which is why removing it is safe and why the fix is two lines.

5. **1.3 cannot dispose the daemon** (see Wave 1 §1.3 below, rewritten).

---

## Headline finding: #3376 is not an epic — the `nodes × databases` footprint is one unscoped code path

The design-note thesis on the issue ("JasperFx.Events needs per-database daemon lifecycle
hooks") is **superseded by recon**. The dominant mechanism behind "497 of 512 databases hold
connections from every node, last statement `COMMIT`/`ROLLBACK`, every backend < 10 minutes
old" is **node-wide durable scheduled-job polling**, which fans out to every tenant database
on every node and was never ownership-scoped. Verified call chain:

1. `WolverineRuntime.Agents.cs:207-231` — in `Balanced` (and `Solo`) mode,
   `startDurableScheduledJobs()` runs **unconditionally on every node**, before and
   independent of any agent assignment.
2. `MessageStoreCollection.StartScheduledJobProcessing` (`MessageStoreCollection.cs:324-330`)
   — `FindAllAsync()` returns **all** stores, one scheduled-jobs agent each.
3. `MultiTenantedMessageStore.StartScheduledJobs` (`MultiTenantedMessageStore.cs:388-394`) —
   `CompositeAgent` over `Source.AllActive()` = **one poller per tenant database**. (Carries
   a `// TODO -- need to start ancillary stores too` that shows this path predates the
   distribution machinery.)
4. `MessageDatabase.StartScheduledJobs` (`MessageDatabase.cs:308-314`) — `new
   DurabilityAgent(...)` + `StartScheduledJobPolling()` directly.
5. `DurabilityAgent.StartScheduledJobPolling` (`DurabilityAgent.cs:262-268`) — timer every
   `ScheduledJobPollingTime` (**default 5s**, `DurabilitySettings.cs:209`).
6. `PostgresqlMessageStore.PollForScheduledMessagesAsync` (`PostgresqlMessageStore.cs:469-525`)
   — fresh pooled connection, `BEGIN`, try advisory lock (`ScheduledJobLockId`), `SELECT`,
   **`ROLLBACK` when quiet** / `COMMIT` when work found, close.

The per-database advisory lock dedupes the *work* across nodes but **not the connection** —
every losing node still opened a connection, began a transaction, and rolled back. At a 5s
cadence each tenant data source keeps ~1 warm connection per node: 512 DBs × N nodes, churned
by Npgsql idle-lifetime pruning. That reproduces every number erdtsieck posted (~937 quiet at
2 nodes, +~350 idle from a third node, `COMMIT`/`ROLLBACK` fingerprint, < 10-min backends).

**The contrast that proves it's an oversight, not a design:**

- Single-database deployments: the distributed durability agent gets
  `AutoStartScheduledJobPolling = true` and runs **on the leader only**
  (`MessageDatabase.Agents.cs:27-31,42-48`).
- Multi-tenant deployments: the per-database durability agents ARE distributed evenly across
  nodes (`MultiTenantedMessageDatabase.Agents.cs:48-52` → `MessageDatabase.BuildAgent:120-123`)
  — but are built **without** scheduled polling, which instead arrives via the unscoped
  node-wide fan-out above.

So the fix is Wolverine-only, patch-sized, and **needs no JasperFx release**: move tenant
scheduled polling onto the already-distributed durability agent and stop the node-wide fan-out
from touching tenant databases.

### What recon ruled OUT as `nodes × databases` sources (evidence in agent reports)

- High-water polling: already ownership-scoped. `JasperFxAsyncDaemon.StopAgentAsync` stops the
  `HighWaterAgent` at the 1→0 agent transition (`JasperFxAsyncDaemon.cs:496-500`), and daemons
  are only materialized for **assigned** agents (`EventStoreAgents.FindDaemonAsync`).
- Neither Marten's sharded tenancy, `MessageDatabaseDiscovery`, leader assignment enumeration,
  nor node heartbeats connect per-tenant — all read the master/pool DB only.
- The #3384 metrics sweeper is correctly owned-only (registers on agent start, unregisters on
  stop) and single-connection-in-flight.
- **No long-lived daemon connection exists to "release."** Marten's `HighWaterDetector` opens
  and disposes a pooled connection per probe; Polecat's takes a bare connection string. The
  connection footprint is governed entirely by *whether polling loops run* — which is exactly
  what the scheduled-polling fix addresses. The original "release the database's pool" idea
  stays dead (pool is shared with app sessions, per the issue's earlier correction).

### Real leaks confirmed (hygiene, not the connection story)

| Leak | Where | Fix side |
|------|-------|----------|
| `EventStoreAgents._daemons` append-only; daemon never released on 1→0 | `EventStoreAgents.cs:16,84` | Wolverine |
| Observer subscriptions to `daemon.Tracker` never disposed (`// TODO -- do we need to care about un-subscribing?`) | `EventStoreAgents.cs:78-82` | Wolverine |
| `_tenantHighWaterTimer` (tenant-partitioned stores only) started in ctor, stopped only in `Dispose()` | `JasperFxAsyncDaemon.cs:91-95,169-170,790-796` | JasperFx |
| `_deadLetterBlock` + throttle semaphores survive 1→0 | `JasperFxAsyncDaemon.cs:99-107` | JasperFx |

Stale claim corrected: the daemon's own `_breakSubscription` IS disposed in `Dispose()`
(`JasperFxAsyncDaemon.cs:171`) — earlier recon predates that fix.

---

## Wave 1 — #3376 fix (Wolverine only) — ✅ DONE, PR #3439 / #3440

Jeremy's calls 2026-07-17: fold into the next planned wave (not its own 6.20.0); no 5.x backport;
immediate release on 1→0; fix + tests in one PR, hygiene separate.

### 1.1 Ownership-scope tenant scheduled polling — ✅ as built

- `MessageDatabase.BuildAgent` and `OracleMessageStore.BuildAgent`: set
  `AutoStartScheduledJobPolling = true` on the distributed agent.
- `MessageDatabase.StartScheduledJobs` / `OracleMessageStore.StartScheduledJobs`: only start a poller
  when `!DurabilityAgentEnabled`. (NOT `MultiTenantedMessageStore.StartScheduledJobs` — see
  correction 2 above. And no "Main store only" carve-out was needed: Main's own distributed agent
  now polls it, on whichever single node owns it.)
- **Back-compat gates (must preserve):**
  - `DurabilityAgentEnabled == false` hosts (`DurabilitySettings.cs:134`; set by mediator-mode
    helpers, `HostBuilderExtensions.cs:486`) have no agent controller — the node-wide fan-out
    is their only scheduled-message pump. Keep the full fan-out when agents are disabled.
  - `Solo` mode starts every agent locally, so tenant polling still runs — verify no
    double-poll window (advisory lock makes overlap harmless; avoid making it permanent).
  - Dynamic tenants: a DB added at runtime gets its agent via `AllKnownAgentsAsync` refresh —
    scheduled polling now follows automatically (today's fan-out only covered stores present
    at startup; the fix makes late tenants *better*, worth a release-note line).
  - `Serverless`/`MediatorOnly`: no change (never started scheduled jobs).

### 1.2 Multi-node connection-scoping test — ✅ as built

`src/Persistence/PostgresqlTests/MultiTenancy/multi_node_tenant_database_connections.cs` (PostgresqlTests,
not SlowTests — that project has no Postgres reference). 2 Balanced nodes, 3 tenant DBs. **Verified red
on main, green with the fix.**

Two hard-won test lessons:

- **`pg_stat_activity` cannot tell two Wolverine nodes apart.** Wolverine never sets
  `application_name`. Under static tenancy the connection strings are ours, so stamp a per-host
  `ApplicationName` and the node→database map falls out.
- **Connection presence ≠ polling.** `AddResourceSetupOnStartup` migrates every tenant DB from *every*
  node at startup and Npgsql parks those in the pool for minutes. A presence-based assertion goes red
  on `main` for the *wrong reason*. Assert that `query_start` advances inside a measurement window,
  using the server's own `clock_timestamp()`.

Exactly-once on a tenant DB is covered. Failover-moves-polling was not built (the reassignment path is
already covered by the 39 green Marten distribution tests).

### 1.3 Leak hygiene — ✅ PR #3440, but NOT as specced

**The daemon cannot be disposed on 1→0.** It is shared: `AllDaemonsAsync()` hands it to Polecat's
`IProjectionCoordinator` (`WolverineProjectionCoordinator.cs:50`) and `TryRebuildRegisteredProjectionAsync`
holds it across a rebuild → disposing is a use-after-dispose for anyone mid-borrow, and per the Marten
9.14 catch-up note driving projections through `IProjectionCoordinator` is the supported path.
Removing it from `_daemons` without disposing is also wrong — `DisposeAsync` sweeps that map at
shutdown, so it would never be stopped at all.

As built: dispose the observer subscriptions on 1→0 and **re-subscribe on reuse** (a cached daemon
handed back after reassignment would otherwise leave observers deaf for the process lifetime); leave
the daemon cached. Retained daemons are bounded by databases-this-node-has-owned and are already
quiesced at 1→0. Full release still wants Wave 2's jfx `StopAndReleaseAsync`.

### 1.4 Issue communication

- Post the draft comment (bottom of this doc) amending the design note — third public
  correction on this issue, same spirit as the previous two.
- Ask erdtsieck to re-measure on the release; predicted outcome: tenant-DB connections from
  non-owner nodes drop to ~zero; steady state ≈ `databases + main-store + app traffic`.
- Optional pre-code confirmation he can run today: `select database, count(*) from pg_locks
  where locktype = 'advisory'` fingerprints the scheduled-poll lock; or raise
  `Durability.ScheduledJobPollingTime` to 5 minutes and watch the parked-connection count
  collapse (also his cheapest interim mitigation, alongside any `Connection Idle Lifetime`
  tuning).

### Explicit non-goals for Wave 1

- No JasperFx.Events lifecycle API (moved to Wave 2, downgraded to hygiene).
- No change to app-session connection demand — the second axis from the issue's correction
  stands; agent scoping never touches it. The Phase-1 budget gauges are the honest measure of
  what remains.

---

## Wave 2 — JasperFx hygiene + pacing seams — ⏳ PR jasperfx#521 OPEN, PARKED awaiting release

**Item 1 (`StopAndReleaseAsync`) was DROPPED as mostly obsolete — the recon behind it was stale.**
`StopAllAsync` already stops high-water, stops/drains the agents, drains `_deadLetterBlock` **and
rebuilds it**, and resets the cancellation source: it is already a restartable quiesce. The only real
leftover was the tenant timer (folded into item 2). And a dispose-everything release would make the
daemon single-use, which collides with callers that cache and reuse it across reassignment
(`IProjectionCoordinator` hands out the same instance; rebuilds hold it across a replay) — the same
constraint that shaped Wave 1 §1.3.

**Item 2 shipped in jasperfx#521**, along with the `ConcurrencyException(string, Exception)` ctor:

- Governors resizable via swap-on-set. Note the asymmetry, documented on each property:
  `BatchWriteThrottle` is a **live pass-through** so it reaches running agents; `_loadThrottle` is
  **captured** into a `ThrottledEventLoader` at agent-build time, so it only applies to agents built
  afterwards. Neither setter disposes the semaphore it replaces.
- `_tenantHighWaterTimer` re-reads `SlowPollingTime` per tick (was captured at construction) and now
  idles when the daemon has no agents.
- `ConcurrencyException(string, Exception)` — unblocks wolverine#3444
  (`SagaConcurrencyException : ConcurrencyException`), which the sagas docs already promise.

Also consolidate `DatabaseServerId` onto `DatabaseDescriptor.Port` (jasperfx#514, merged
2026-07-15) when the JasperFx pin bumps — noted in the Phase-1 plan as deferred.

---

## Wave 3 — #3397 Phase 2: adapt (Wolverine 6.20.x+, AFTER erdtsieck re-baselines on Wave 1)

Wave 1 removes most of the pressure #3397 exists to relieve — re-measure before tuning
(erdtsieck said exactly this on the issue). Everything below is **default-off**; ship order
within the wave is 3.1 → 3.2 → 3.3, and 3.4 can lag.

### 3.1 `ConnectionBudgetMonitor` (Wolverine core)

- Lives in `PersistenceMetricsSweeper` — the per-node singleton every snapshot already flows
  through (`PersistenceMetricsSweeper.For(runtime)`, `PersistenceMetricsSweeper.cs:23-28`).
  Attach at the publish point (`probeConnectionBudgetsAsync`, lines 246-252); extend the
  existing per-server `ServerBudgetState` (line 47) with EMA + water-line state. Single-loop
  thread ⇒ no locking (existing invariant).
- Config on `DurabilitySettings` beside `ConnectionBudgets` (line 300): `HighWaterUtilization`
  (~0.70), `LowWaterUtilization` (~0.55), smoothing window, `BackoffFactor`, `Enabled`.
  Hysteresis + EMA are mandatory (single threshold flaps).
- **Repeated probe failure = maximal pressure** — the marked catch block
  (`PersistenceMetricsSweeper.cs:229-241`) engages backoff instead of only logging. This is
  the reporter's db-apply failure mode.
- Exposes `PacingFactorFor(DatabaseServerId)`; keyed off `ConnectionBudgetSnapshot.Utilization`
  (`ConnectionBudgetSnapshot.cs:53`, comment already points here). Log state transitions;
  include budget state in `IDescribeMyself`.

### 3.2 Wolverine actuation — DurabilityAgent re-reading loops

Convert the recovery timer (`DurabilityAgent.cs:94-114`) and scheduled-job timer (`:262-268`)
from fixed-period `System.Threading.Timer` to the sweeper's re-read-per-pass loop model
(`PersistenceMetricsSweeper.cs:112-166`): each iteration,
`effective = ScheduledJobPollingTime × PacingFactorFor(_database.ServerId)`. The agent already
holds `_runtime` and the store resolves its `ServerId` via `IConnectionBudgetProbe`.
(Expiration and handled-cleanup timers stay as-is — hourly/minutely, not pressure drivers.)
Note: after Wave 1, scheduled polling runs inside these same distributed agents, so pacing
automatically covers it.

### 3.3 Daemon cadence actuation — no JasperFx release required

Two live seams already exist:

- `FastPollingTime`/`SlowPollingTime` are re-read by the high-water loop **every wait**
  (`HighWaterAgent.cs:115,140,...`) on the live `ProjectionGraph` instance.
- `DaemonSettings.Wakeup` (`IDaemonWakeup`, `DaemonSettings.cs:109`) wraps every inter-poll
  wait — a Wolverine-supplied implementation can scale the effective delay by the pacing
  factor without mutating shared settings.

Prefer the `IDaemonWakeup` route (formal hook, no cross-daemon settings mutation). Gap to
document: the tenant high-water timer doesn't consult either seam until Wave 2 item 2 lands.

### 3.4 Governor pacing (needs Wave 2 item 2)

Scale `MaxConcurrentEventLoadsPerDatabase`/`MaxConcurrentBatchWritesPerDatabase` down under
sustained pressure via the resizable gates. Cadence pacing (3.2/3.3) ships first and alone if
the JasperFx release lags — it delivers most of the relief (connections are held by polling,
not by governor width).

### 3.5 CritterWatch

Consume budget state transitions (engaged/released) on ServiceUpdates; alert on sustained
high-water. The Phase-3 control-queue pacing commands stay parked (post-1.0, as told to
erdtsieck).

### Definition of done (Phase 2)

- [ ] Utilization ≥ high-water for M consecutive probes ⇒ polling cadence stretches by
      `BackoffFactor` on that server's databases only; ≤ low-water ⇒ restores. Pinned by test
      with a fake probe.
- [ ] Probe failure streak ⇒ same engagement (test).
- [ ] No flapping across the hysteresis band under a noisy fake probe (test).
- [ ] Default-off; zero behavior change when disabled (test: pacing factor pinned at 1.0).
- [ ] Docs: budget page grows an "adaptive back-off" section; pooler caveat repeated.

---

## Sequencing & release map

| Wave | Ships in | Gate |
|------|----------|------|
| 1 — #3376 scheduled-polling scoping + hygiene + test | Wolverine 6.20.0 | none — ready to build on plan approval |
| 2 — JasperFx quiesce + resizable governors | next JasperFx (2.28.0?) | independent; only gates 3.4 |
| 3 — #3397 Phase 2 adapt | Wolverine 6.20.x/6.21.0 | erdtsieck re-baseline on Wave 1 (his own sequencing ask) |

## Risks

- **Wave 1 diagnosis risk**: the chain is code-verified, but production has surprised us
  before (BASELINE BEFORE YOU BLAME). Mitigation: the multi-node test in 1.2 reproduces the
  both-nodes fingerprint on `main` *before* the fix and shows it gone after; plus erdtsieck's
  optional `pg_locks` confirmation.
- **Scheduled-message latency on reassignment**: after a node dies, a tenant DB's scheduled
  poll pauses until its durability agent is reassigned (heartbeat-order delay). Today's
  redundant pollers masked that. Acceptable — same semantics recovery already has — but
  release-note it.
- **`DurabilityAgentEnabled=false` + multi-tenant**: keep-fan-out path must be tested or the
  fix silently kills scheduled messages for mediator-style hosts.
- **Phase 2 flapping/EMA tuning**: fake-probe tests, not production, are where the constants
  get exercised first.

## Open questions for Jeremy

1. Wave 1 target: 6.20.0 as its own release, or fold into the next planned wave?
2. On 1→0 daemon release (1.3): release immediately on revocation, or after a grace period
   (heartbeat interval) to avoid rebalance thrash? Design note promised "short grace" —
   immediate is simpler and `FindDaemonAsync` rebuilds cheaply; recommend immediate.
3. Phase 2 naming interview (standing convention) before 3.1 ships: `BackoffFactor` vs
   `PacingFactor`, config section name, transition log wording.
4. Does the scheduled-polling fix warrant a 5.x backport? Same code shape exists on the 5.0
   branch, and it's arguably a defect, not a feature.

---

## Comment for #3376 — ✅ POSTED 2026-07-17

https://github.com/JasperFx/wolverine/issues/3376#issuecomment-5002738071

Rewritten after the two-node test, then posted on Jeremy's go-ahead. The old draft's "single-database
already does this right" paragraph was based on dead code and was cut — see correction 1 at the top of
this doc. Everything below is backed by a test that is red on `main` and green with the fix (PR #3439).
As posted, it also links #3439 and #3440.

> ## Root cause found, fixed, and it's a patch rather than an epic
>
> Thanks for the registration and `pg_stat_activity` answers — they ruled out the coordinator footgun
> and pointed us straight at what else touches every tenant database from every node. I owe this issue
> a third correction: the per-database daemon lifecycle hooks I sketched are **not** the fix. The
> daemon side was already ownership-scoped, and neither the daemon nor the high-water detector holds a
> long-lived connection to release.
>
> **The mechanism is durable *scheduled-message* polling.** Under multi-tenancy Wolverine started a
> scheduled-job poller for **every tenant database on every node**, entirely outside the
> agent-distribution machinery. Every `ScheduledJobPollingTime` (default 5s), per database, per node:
> open a pooled connection, `BEGIN`, try a per-database advisory lock, `SELECT` for due messages, then
> `ROLLBACK` (quiet) or `COMMIT` (work found). The advisory lock dedupes the *work* across nodes but
> not the *connection* — the losing node still opened, lock-failed, and rolled back. That is your
> fingerprint exactly: `COMMIT`/`ROLLBACK` last-statements, ~every database connected from every node,
> every backend younger than your idle-lifetime pruning, and each added node contributing another ~512
> parked connections while "idle".
>
> Rather than take that from a code reading, we pinned it: a two-node test against sharded tenant
> databases, with each node stamping its own `application_name` so `pg_stat_activity` can attribute
> every connection. On `main`, agent distribution is perfectly clean — each tenant database is owned by
> exactly one node — and **every tenant database is still queried by both nodes anyway**. The
> connections were never coming from the agents.
>
> **What it turned out to be:** this is [#2623](https://github.com/JasperFx/wolverine/issues/2623),
> never applied to the relational stores. The RavenDb and CosmosDb stores already return a *non-started*
> agent from this path, with a comment explaining that the node agent controller owns the durability
> agent's lifecycle and starting a second poller here would double up. The Postgres/SQL Server/Oracle
> family kept starting its own. The fan-out's agent is never actually started by the runtime, so that
> eager poll timer was its entire contribution — which is why the fix is small.
>
> **The fix** (Wolverine-only, no JasperFx release needed): scheduled polling rides the per-database
> durability agent that managed distribution already assigns to exactly one node. The node-wide fan-out
> stops starting pollers, except for hosts running without durability agents, where it's still the only
> pump. Expected steady state on your topology: tenant-DB connections from non-owner nodes ≈ 0, total ≈
> `databases + main store + your application traffic` — and adding a node finally *divides* the polling
> load instead of multiplying it. Dynamic tenants get polling automatically now, where the old fan-out
> only covered databases present at startup.
>
> The application-traffic axis from my earlier correction still stands and is untouched by this; the
> per-server budget gauges from 6.19.0 should give us both an honest before/after.
>
> One tradeoff worth naming: when a node dies, a tenant database's scheduled poll now pauses until its
> durability agent is reassigned, where the redundant pollers used to mask that. It's the same
> semantics message recovery already has, but it's a real change.
>
> If you want to confirm before the release: `select count(*) from pg_locks where locktype = 'advisory'`
> across your tenant DBs fingerprints the pollers, and raising `opts.Durability.ScheduledJobPollingTime`
> is a crude interim relief valve (at the cost of scheduled-message latency).
>
> We're also fixing a smaller leak the recon confirmed — tracker subscriptions that were never disposed
> when a node stopped owning a database — and the #3397 adaptive budget work resumes once this lands and
> you've re-baselined, per your own sequencing.
