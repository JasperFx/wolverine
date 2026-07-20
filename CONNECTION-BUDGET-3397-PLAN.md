# Connection-budget awareness plan (wolverine#3397)

> **STATUS 2026-07-18 — Phase 1 SHIPPED (6.19.0); #3376 fix SHIPPED (6.20.0); both issues CLOSED.**
> **The only unfinished Phase-1 deliverable is the CritterWatch consumer.** Phase 2 stays parked
> pending erdtsieck's re-baseline on 6.20.0 (per the closing notes on both issues; when it resumes,
> file a fresh focused issue rather than reopening #3397).
>
> | Repo | PR | State |
> |------|----|-------|
> | wolverine | [#3422](https://github.com/JasperFx/wolverine/pull/3422) — Workstreams A/B/C + P(b), docs, 22 tests | **merged, shipped in 6.19.0** |
> | jasperfx | [#514](https://github.com/JasperFx/jasperfx/pull/514) — `Port` on `DatabaseDescriptor` | **merged, released**; `DatabaseServerId.From` now reads `descriptor.Port` (consolidation done) |
> | jasperfx | [#521](https://github.com/JasperFx/jasperfx/pull/521) — resizable governors + tenant-HWM re-read + `ConcurrencyException` ctor | **merged, in V2.28.0** → Wolverine pins 2.29.0, so Wave 2 is fully released and Wave 3.4's gate is cleared (also unblocks wolverine#3444) |
> | polecat | none needed | Workstream P resolved as (b) — no sharded tenancy, so nothing to activate |
> | CritterWatch | **still to do** | pin block gone (CW is on 6.20.0/2.29.0) but no `ConnectionBudget` consumer exists in CW source — budget snapshots are published and dropped on the floor |
>
> Issue timeline: plan posted 7/14; #3376 root-caused + fixed (#3439/#3440) 7/17; 6.20.0 shipped
> 7/17; #3376 and #3397 both closed 7/17 with re-baseline deferral notes.
>
> **Open questions, now answered (Jeremy, 2026-07-13):**
> 1. Workstream P → **(b)**, as the plan leaned. SQL Server gets probe plumbing + gauge parity; activation follows Polecat's tenancy roadmap.
> 2. Config surface → **fluent per-server**: `Durability.ConnectionBudgets.ForServer(host, port, maxConnections)`.
> 3. Naming → `wolverine-database-connection-count` (used) / `wolverine-database-connection-budget` (max), tag `server`. DTO `ConnectionBudgetSnapshot`, observer member `ConnectionBudget`.
> 4. Port gap → **derived in Wolverine's key**, not waiting on the descriptor. `DatabaseServerId(Engine, ServerName, Port?)` reads the port off the store's own connection-string builder, so 6.19.0 ships on the current JasperFx pin. jasperfx#514 closes the gap at the descriptor level for the rest of the ecosystem; Wolverine consolidates onto it later.
>
> **Deviation from the plan as written:** `DatabaseServerId.Port` is `int?`, not `int`. SQL Server's
> `Data Source` already carries the port (`host,1433`) or a named instance (`host\SQLEXPRESS`), so it
> leaves Port null and keeps the DataSource whole rather than inventing a split that would be ambiguous.


Working plan for [wolverine#3397](https://github.com/JasperFx/wolverine/issues/3397) —
"Adaptive connection-budget awareness: probe server max_connections/numbackends and back off
daemon polling/concurrency under pressure (CritterWatch-surfaced)". Reporter: @erdtsieck, same
512-tenant-database deployment as #3375/#3384 (shipped) and #3376 (parked).

**Decisions from Jeremy (2026-07-13, binding):**

1. Phase 1 (measure + surface) targets **Wolverine 6.19.0** — NOT the current 6.18.0 wave.
2. **Assume a pooler (pgBouncer) in front** for the immediate work → the connection budget's
   `MaxConnections` is **explicit configuration**, not a probed `pg_settings` value. Probing
   `max_connections` is at most a fallback/diagnostic when no explicit value is given.
3. **Engine parity from the start**: PostgreSQL AND SQL Server → brings **Polecat** into scope.
4. Server identity derives from the existing **`DatabaseDescriptor`** metadata (JasperFx
   `Descriptors/DatabaseDescriptor.cs` — `ServerName`/`DatabaseName`/`DatabaseUri()`), not a new
   connection-string parser.
5. The budget machinery is **only active when using Marten's sharded-database tenancy**
   (`StoreOptions.MultiTenantedWithShardedDatabases(...)`) — the many-databases-per-server shape
   is the only one where per-server budgeting pays for its complexity.

---

## Ground truth from code recon (2026-07-13)

Findings that reshape the issue as filed:

- **The knobs the issue names are NOT Wolverine's.** `SlowPollingTime`/`FastPollingTime`,
  `MaxConcurrentEventLoadsPerDatabase`, `MaxConcurrentBatchWritesPerDatabase` all live in
  JasperFx.Events `DaemonSettings` (`~/code/jasperfx/src/JasperFx.Events/Daemon/DaemonSettings.cs:78-133`
  — the epic #486 WS2/WS3 governors). The "adapt" half is therefore mostly a **JasperFx.Events
  feature with Wolverine wiring**, pinned to a JasperFx release (2.28.0+ — same release already
  earmarked for the #3376 lifecycle hooks).
- **Runtime mutability is split.**
  - `HighWaterAgent` re-reads `FastPollingTime`/`SlowPollingTime` on every wait
    (`HighWaterAgent.cs:115,140`) → daemon cadence is adaptable *today* by mutating settings.
  - The #486 governors are `SemaphoreSlim`s sized at construction (`ThrottledEventLoader.cs:16-18`)
    → resizing under pressure needs real JasperFx work (adaptive gate), not a settings tweak.
  - Wolverine's `DurabilityAgent` captures `ScheduledJobPollingTime` once into
    `System.Threading.Timer`s (`src/Persistence/Wolverine.RDBMS/DurabilityAgent.cs:114,267`) →
    needs a re-reading loop (the #3384 sweeper already models this: `PersistenceMetricsSweeper.cs:100`
    re-reads `UpdateMetricsPeriod` per pass).
- **No server probe exists anywhere.** Nothing queries `pg_settings`/`pg_stat_database`;
  `max_connections` appears only in comments (`AssignmentGrid.Distribution.cs:108`).
  `PostgresqlMessageStore.FetchCountsAsync` (`PostgresqlMessageStore.cs:216`) already does
  `pg_catalog` introspection — the template for the probe.
- **No "server" concept exists.** Grouping today is per logical database
  (`AssignmentGrid.DistributeByGroupAffinity`, `EventSubscriptionAgentFamily.cs:183`). But
  `IMessageStore.Describe()` (`IMessageStore.cs:131`) returns a `DatabaseDescriptor` whose
  `ServerName` is filled from the connection string host (`PostgresqlMessageStore.cs:558`), and
  `DatabaseId(descriptor.ServerName, descriptor.DatabaseName)` already exists
  (`PostgresqlMessageStore.cs:53`) → **server key = descriptor-derived**, per decision (4).
  - ⚠️ Gap: `ServerName` is host only — **no port**. Two Postgres clusters on one host on
    different ports would collide. Fold port into the descriptor (or the derived key) as part of
    Phase 1.
- **The CritterWatch seam** is `IWolverineObserver.PersistedCounts` published from the #3384
  sweeper (`PersistenceMetricsSweeper.cs:126`). The sweeper is sequential per node → deduping the
  probe per server key within a pass is natural. OTel side:
  `PersistenceMetrics` `ObservableGauge`s (`PersistenceMetrics.cs:41-46`).
- **Polecat has no sharded-tenancy analog** (no `sharded` hits in `~/code/polecat/src`). Parity
  scope for SQL Server needs its own trigger-condition definition (see Workstream P).

## Relationship to #3376 (parked)

#3376 (same reporter) is parked awaiting his answers on (a) whether `AddAsyncDaemon` is registered
alongside managed distribution (config-bug hypothesis that would explain the ~1,400-connection
baseline) and (b) the connection breakdown in `pg_stat_activity`. Two consequences:

- **Phase 1 of this plan is the instrument that answers (b).** A per-server used/max gauge on the
  metrics feed is pure observability, independent of #3376's resolution, and gives both sides the
  data. Ship it first.
- **Phase 2 (adapt) waits** until #3376 resolves — the reporter himself scoped his prototype offer
  as "once #3376 lands", and the owned-agent scoping changes the baseline any tuning works against.
  The JasperFx hooks for both issues should ride the same JasperFx release.

---

## Phase 0 — Reply + triage (now, no code)

- [ ] Post the drafted reply on #3397 (draft at bottom of this doc; Jeremy reviews wording first).
- [ ] Confirm the reporter's topology (pooled vs direct) — the reply asks.
- [ ] Re-point at the outstanding #3376 questions (baseline dependency).

## Phase 1 — Measure + surface (Wolverine 6.19.0 + CritterWatch)

Scope: **observability only.** No behavior changes. Opt-in by construction: only wired up when the
message store's tenancy reports the sharded shape.

### Workstream A — server identity (Wolverine core)

1. Server key type (e.g. `DatabaseServerId`) derived from `DatabaseDescriptor`
   (`Engine` + `ServerName` [+ port — close the gap noted above]). Lives next to `DatabaseId`.
2. Group registered stores/databases by server key inside the sweeper pass — no new registry;
   the sweeper already walks every registered store sequentially.

### Workstream B — the probe (Wolverine.Postgresql / Wolverine.SqlServer)

3. New capability interface (e.g. `IConnectionBudgetProbe`, sibling of `IMessageStoreAdmin`):
   `ValueTask<int> CountServerConnectionsAsync(CancellationToken)` — one cheap query per server
   per sweep interval, deduped by server key.
   - Postgres: `select sum(numbackends) from pg_stat_database` (visible without special grants).
   - SQL Server: `select count(*) from sys.dm_exec_connections` — requires `VIEW SERVER STATE`;
     degrade loudly-but-safely (log once, report budget as unknown) when permission is missing.
4. `MaxConnections` is **explicit config, per server key** (decision 2 — pooler in front makes the
   probed server value misleading). Optional fallback when unconfigured: probe
   `pg_settings.max_connections` / `@@MAX_CONNECTIONS` once at startup, tagged as "probed" in
   diagnostics so operators can tell the two apart.
5. Config surface (shape TBD in review): something like
   `DurabilitySettings.ConnectionBudgets.ForServer(host, maxConnections)` or a callback keyed by
   `DatabaseServerId`. Must be settable per server because one deployment can span servers with
   different budgets.

### Workstream C — surfacing (Wolverine core + CritterWatch)

6. New DTO (e.g. `ConnectionBudgetSnapshot { serverKey, used, max, source(probed|configured) }`)
   published via a new `IWolverineObserver` method alongside `PersistedCounts`
   (`IWolverineObserver.cs:42`) from the sweeper pass. Keep it a sibling of `PersistedCounts`, not
   a member — the budget is server-scoped, counts are database-scoped.
7. OTel `ObservableGauge`s in `PersistenceMetrics` tagged by server key
   (`wolverine_database_server_connections_used` / `_max` or similar — naming interview with
   Jeremy before shipping).
8. CritterWatch: consume on the ServiceUpdates feed; used/max per server on the service view;
   alert threshold. Rides whatever CW beta follows Wolverine 6.19.0.

### Workstream P — Polecat / SQL Server parity

9. Polecat has **no sharded-database tenancy today**. Parity decision needed at design review:
   - (a) implement the probe + budget surfacing against Polecat's existing multi-tenant shapes
     (master-table tenancy), keyed by the same descriptor-derived server id; or
   - (b) hold SQL Server activation until Polecat grows the sharded feature, shipping only the
     probe plumbing (`IConnectionBudgetProbe` on `SqlServerMessageStore`) now.
   Leaning (b) for 6.19.0 — plumbing + gauge parity, activation condition follows Polecat's
   tenancy roadmap. **Confirm with Jeremy.**

### Definition of done (Phase 1)

- [ ] Sharded-Marten host publishes per-server used/max on the metrics feed and OTel meters.
- [ ] Probe runs once per server per sweep pass regardless of database count (assert in a test
      with N databases on one server → 1 probe query).
- [ ] Explicit `MaxConnections` config honored; probed fallback clearly labeled.
- [ ] SQL Server probe parity (per Workstream P resolution).
- [ ] Docs page (pooler caveat prominent: behind a transaction-pooling pgBouncer, server
      `numbackends` ≠ client connections — explain what the number means in each topology).
- [ ] CritterWatch surfacing + alert.

## Phase 2 — Adapt (opt-in; JasperFx 2.28.0+ + Wolverine 6.19.x; AFTER #3376 resolves)

The reactive half. Everything below is **default-off**.

1. `ConnectionBudget` policy on `DurabilitySettings`: `Enabled`, `HighWaterUtilization` (~0.70),
   **`LowWaterUtilization` for hysteresis** (~0.55 — a single threshold flaps), `BackoffFactor`,
   smoothing (EMA over the last few probes).
2. Node-local `ConnectionBudgetMonitor` per server key. Each node adapts independently off the
   shared server signal — no cross-node consensus. (The durability sweeper/agents give every node
   that hosts agents a local probe already, from Phase 1.)
3. **Probe-failure semantics: repeated probe failure = maximal pressure** (can't get a connection
   IS the signal — this is exactly the reporter's db-apply failure). Engage backoff, don't treat
   as missing data.
4. Wolverine actuation: migrate `DurabilityAgent`'s recovery/scheduled timers to a re-reading
   loop; scale the effective `ScheduledJobPollingTime` by the backoff factor under pressure.
5. JasperFx actuation (rides the same 2.28.0 hooks planned for #3376):
   - pacing multiplier consulted by `HighWaterAgent` waits (works today via settings mutation —
     formalize as a hook rather than mutating shared settings);
   - adaptive gate for the WS2/WS3 governors (`ThrottledEventLoader` + batch-write semaphore) —
     these must be restructured to resize; real work, not a tweak.
6. Log state transitions (engaged/released, with utilization) and include budget state in
   `IDescribeMyself` output.

## Phase 3 — CritterWatch control loop (deferred, note-only)

Explicit pacing commands over CW's existing control queue ("this server is under reporting load,
back off") — human/automated override closing the measure → show → act loop. Post-1.0 CW
territory; acknowledged in the issue reply, not scoped.

---

## Risks / caveats

- **Pooler topologies**: probed `max_connections` is meaningless behind pgBouncer → explicit
  config is primary (decision 2). Docs must spell out what `numbackends` measures in
  session/transaction pooling modes.
- **`numbackends` counts ALL backends**, including other applications — that's the point
  (server-scoped resource) but must be documented so nobody expects Wolverine-only attribution.
- **`ServerName` lacks port** — fix in the descriptor or key derivation, else co-hosted clusters
  collide.
- **SQL Server permission**: `VIEW SERVER STATE` may be absent in locked-down hosting; degrade to
  "budget unknown" without failing the sweep.
- **Semaphore governors can't shrink live** — Phase 2 JasperFx work is structural.
- **Flapping** — hysteresis + EMA are mandatory, not nice-to-have.

## Open questions

1. Workstream P (a) vs (b) — how much Polecat activation in 6.19.0? (Leaning (b).)
2. Config surface shape for per-server `MaxConnections` (fluent per-host vs callback).
3. Metric/DTO naming — interview Jeremy before shipping (standing convention).
4. Does the budget snapshot also feed the `db-apply`-style out-of-band tools (reporter's failure
   was a schema apply job, not the daemon)? Possibly a small static helper those jobs can call —
   out of scope for 6.19.0 unless Jeremy wants it.

---

## Draft reply for #3397 (Jeremy reviews before posting)

> Thanks — this direction is a keeper, and the db-apply datapoint is exactly the failure mode
> worth designing against. A few notes on how we're planning to slice it, plus two questions.
>
> **We're splitting it into "measure + surface" first, "adapt" second.**
>
> The measurement half — a per-server used/max connection budget on the node metrics feed and
> through CritterWatch — doesn't depend on anything else and is slated for **Wolverine 6.19.0**.
> It also happens to be the instrument that answers the connection-breakdown question still open
> on #3376, so shipping it first helps both issues. It'll be keyed off the database server
> identity we already carry in the `DatabaseDescriptor` metadata, deduped so it's one cheap query
> per server per metrics pass no matter how many tenant databases live there, and scoped to the
> sharded-database tenancy model — that's the shape where per-server budgeting earns its keep.
> We'll do PostgreSQL and SQL Server parity from the start.
>
> One design decision to flag: **the max side of the budget will be explicit configuration, not
> the probed `pg_settings` value.** Any pooler in front (pgBouncer et al.) makes the server's
> `max_connections` misleading as a client budget, so you'll declare the budget per server and
> the probe supplies the `used` side (`sum(numbackends)`). A probed max may serve as a labeled
> fallback when nothing is configured. **Question 1: is your claims server talking to Postgres
> directly, or through pgBouncer/a pooler?** That affects which numbers we document as meaningful
> for your topology.
>
> The adaptive half needs to wait on two things. First, the knobs you named —
> `SlowPollingTime`/`FastPollingTime` and the #486 concurrency governors — actually live in
> JasperFx.Events' `DaemonSettings`, not Wolverine, so the back-off hooks have to ship in a
> JasperFx release (the same one already earmarked for the #3376 lifecycle work). Second, as you
> anticipated, #3376's resolution changes the baseline any tuning would work against — so
> **question 2 is the same two questions still open over there** (the `AddAsyncDaemon`
> registration, and the `pg_stat_activity` breakdown). Those answers gate the whole adapt phase.
>
> Two refinements we'll bake into the adaptive design when it comes: hysteresis (high/low water
> marks plus smoothing, so a single threshold doesn't flap), and treating repeated probe *failure*
> as maximal pressure rather than missing data — a probe that can't get a connection is the
> strongest possible signal, and it's precisely your db-apply scenario.
>
> The CritterWatch control-queue pacing commands are a good closing-the-loop idea; we're noting it
> as a later phase rather than scoping it now.

---

## Sequencing note

Current wave (6.18.0 / jfx 2.28.0 / CW beta.4 per `NEXT-WAVE-HANDOFF.md`) is **unaffected** —
this plan starts at 6.19.0. If the #3376 answers arrive mid-wave and turn out to need the jfx
hooks anyway, design the hook surface with Phase 2's pacing needs in mind so one JasperFx release
serves both.
