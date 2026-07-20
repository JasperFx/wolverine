# Next-wave execution handoff (state as of 2026-07-13 ~00:25 UTC)

Self-contained handoff. Supersedes `NEXT-WAVE-RELEASES-PLAN.md` (kept for background).
**Track A is DONE — Wolverine 6.17.3 is cut.** Track B is **parked pending a reporter answer** (the
#3376 design does not survive contact with the code — see below). Track D has started.

**Publishing policy (standing, from Jeremy):** JasperFx = automatic NuGet publish on green merge.
Marten = local-feed verification gate (verify Wolverine AND CritterWatch against the packed
candidate) before publishing. Wolverine ships via `publish_nugets.yml` + `V<ver>` tag; CritterWatch
via `publish-nuget.yml`; JasperFx/Marten via `on-manual-do-nuget-publish.yml` (`--ref main` /
`--ref master`). Merge gate everywhere: watch `gh pr checks` manually, **never `gh pr merge --auto`**.

**Announcements (standing, from Jeremy, 2026-07-12):**
- **Discord**: announce every release. There is **no webhook/tool available** — Claude DRAFTS the
  message, **Jeremy pastes it**. Announce **as soon as the publish workflow is green** (do NOT wait
  for the nuget.org index to catch up).
- **GitHub release notes**: for every release, call out **all** issues and PRs that were part of it.
  The V6.17.3 notes are the established shape (Closed issues / Fixes from review / Docs, with
  contributor attribution).

**Scope decisions from Jeremy (do not relitigate):**
- **wolverine PR #3387** ([Entity] load profiles, issue #3367): **NOT taking in at this time.**
  Leave PR + issue open. The contributor has not been told — check with Jeremy before posting.
- CritterWatch 1.1-milestone items and post-1.0-labeled epics stay untouched.

## Repos

| Repo | Path | Notes |
|---|---|---|
| Wolverine | `~/code/wolverine` | `main`; push to `ghhttps` (`git fetch ghhttps main && git merge --ff-only ghhttps/main`) |
| JasperFx | `~/code/jasperfx` | `main` |
| Marten | `~/code/marten` | shared tree on a feature branch — **worktrees only** |
| CritterWatch | `~/code/CritterWatch` | `main` |

---

## Track A — DONE. Wolverine 6.17.3 shipped

**9 PRs merged, `V6.17.3` tagged, `publish_nugets.yml` run 29214705779, GitHub release notes published.**

| Merged | What |
|---|---|
| #3364 | SNS per-tenant LocalStack fix (closed #3332) |
| #3384 | Metrics sweeper (closed #3375) |
| #3386 | gRPC saga coverage (refs #3385) |
| #3370 | RabbitMQ listener ghosting |
| #3389 | gRPC + Sagas docs page |
| #3390 | TrackedSession ignores `INotToBeRouted` (ProductSupport#33) |
| #3393 | Sweeper unregistration race + `UpdateMetricsPeriod` guard |
| #3394 | #3388 cold-path coverage + honor tracked-session timeout |
| #3395 | Testing-docs note |

Also: CritterWatch PR #701 merged (#689 docs half; **#689 stays open** for the second half).
Filed **#3391** (RabbitMQ follow-ups: tracking-invariant test + eager-restart re-declare gap).
Closed #3392 as a duplicate of #3391.

### Remaining Track A tail
- [ ] **Discord announcement for 6.17.3** — draft written in-session; hand to Jeremy on green publish.
- [ ] **Reply + close [ProductSupport#33](https://github.com/JasperFx/ProductSupport/issues/33)**
      once 6.17.3 is resolvable. Fixed by #3390; `IgnoreMessagesMatchingType` remains the workaround
      for ≤ 6.17.2.

### 6.17.3 behavior change worth watching
`PauseThenCatchUpOnMartenDaemonActivity` now **honors the tracked session's timeout**. A test that
previously waited silently up to 60s now fails fast, telling the user to raise
`TrackActivity().Timeout(...)`. Correct, but expect questions.

---

## Track B / C — #3376 is PARKED. The design note is partly wrong.

**Do not start building JasperFx 2.28.0 lifecycle hooks until @erdtsieck answers on #3376.**
Findings (posted as a comment on the issue, 2026-07-13):

1. **"Release the database's connection pool on revocation" is NOT implementable as specified.**
   Marten's `NpgsqlDataSource` is owned by the tenancy's `MartenDatabase`, and under
   database-per-tenant that same data source serves **ordinary application sessions** for that
   tenant on that node. Disposing it on agent revocation would abort live app connections. There is
   no "is anything else on this node using this DB" signal, and Marten has no API to evict a single
   tenant database (`RefreshAsync()` blanks the cache **without disposing** → would leak data sources).
2. **Jeremy's correction, and it generalizes:** command/message processing opens connections to
   **any** tenant database regardless of async-daemon affinity. So daemon scoping alone **cannot**
   reach `databases + overlap` — the app's own tenant traffic is a second, independent axis of
   connection demand.
3. **The high-water polling loop already stops** on last-agent-stop
   (`JasperFxAsyncDaemon.StopAgentAsync`). The "only the owning node polls this database" half
   largely exists today.
4. **Live footgun that would fully explain the reported 1,300 connections:**
   `EventStoreAgents.StartAllAsync()` / `AllDaemonsAsync()` blanket-materialize a daemon for **every**
   database and start **every** shard — no ownership check. `WolverineProjectionCoordinator` is
   deliberately registered as a plain singleton so this never runs at bootstrap — **but
   `IProjectionCoordinator` is itself an `IHostedService`**, so an app that also calls
   `AddAsyncDaemon(Solo|HotCold)` alongside managed distribution gets `StartAsync` → `StartAllAsync`
   → every node starts every shard on every database. That is exactly `nodes × databases`.

**Asked @erdtsieck for:** (a) his registration — is `AddAsyncDaemon(...)` present alongside
`UseWolverineManagedEventSubscriptionDistribution`? (b) what fraction of the ~1,300 connections in
`pg_stat_activity` are the high-water query vs ordinary application traffic.

If (a) is **yes** → this is a **config bug**: fix = startup guard + diagnostic, no JasperFx release
needed. If **no** → re-scope the hooks around what is actually achievable (daemon quiesce, **not**
pool release).

**Real leaks worth fixing regardless** (found in recon, currently unticketed):
- `EventStoreAgents._daemons` is append-only — daemons are never released.
- `JasperFxAsyncDaemon`'s per-database `System.Timers.Timer` is started in the **constructor** and
  only stopped in `Dispose()` — `StopAllAsync` does not touch it.
- The daemon's subscription to the database's `ShardStateTracker` is never disposed; a rebuilt daemon
  re-subscribes and both observers stay attached.

### Other Track C items (independent of #3376, still valid for 6.18.0)
- **#3385 scoped diagnostic**: replace the opaque `IndeterminateSagaStateIdException` over a gRPC hop
  with "header-identified saga over gRPC is not supported; put the saga identity on the message
  body", and flip the characterization test
  `starting_a_header_identified_saga_over_grpc_fails_with_opaque_status_today`
  (`src/Wolverine.Grpc.Tests/SagaOverGrpc/saga_over_grpc_tests.cs`). Link the gRPC sagas docs page
  (shipped in #3389).
- **#698 upstream half**, if Track D's investigation confirms one.

---

## Track D — CritterWatch beta.4

The **#698 investigation is IN FLIGHT** (leading hypothesis: an agent-store vs restriction-store
mismatch — event-subscription agents live in an ancillary Marten store while restrictions write to
the main store, which would explain `wolverine_agent_restrictions` having 0 rows).

1. **#698 Pause-acks-success-but-never-pauses**: fix the Wolverine side if broken (→ Track C /
   6.18.0). **CritterWatch side regardless**: the pause handler must verify observed reality
   (restriction row present, or agent actually stopped) before acking Succeeded. A green ack for a
   no-op is worse than a visible failure.
2. **#697**: mirror the `ClearAlert` pattern for Acknowledge/Snooze in `AlertCommandHandler` (actor
   from envelope principal w/ UI fallback, on the events + `AlertRecord`, plus `auditLog.LogAsync`).
   Add the `/audit` → `/audit-log` route alias (same class as #693's `/dead-letters` → `/dlq`).
3. **#699 + #636 together** (same timeline/event-feed surface): materialize a timeline entry only on
   (agent → node) assignment CHANGE; dedupe consecutive identical entries; retention for
   `TimelineEntry` docs (reuse the #468/#695 approach — and per the #685 lesson, any new table shape
   must handle upgraded stores loudly). Then #636's Recent Events widget defects on top.
4. **#689 second half**: evaluate deferring the capability snapshot's ApiExplorer read
   (`ServiceCapabilities.ReadFrom` → `OpenApiDescriptorBuilder.TryBuildForWolverine`) until
   `ApplicationStarted`. Close #689 either way (docs half was PR #701).
5. **#610**: audit COMPLETE → **close with evidence + file the follow-ups** (see addendum below).
6. **Pull-in candidates if room**: #632 explorer navigability; #670 sequence-diagram click-through;
   #347 manual-test walkthrough (PRIORITY-tagged, pure writing).
7. Pins (Wolverine 6.18.0, JasperFx 2.28.0 if it ships, Marten if released) → **beta.4** → ask
   erdtsieck to re-verify #697/#698/#699 + the round-1 fixes; nudge him onto the beta.3/6.17.2+
   baseline first (his round-2 reports were against beta.2).

---

## Sweep result (2026-07-12, pre-release, all 5 repos)

Marten, Polecat, CritterWatch, ProductSupport: **clean** — no regressions from Marten 9.15.0 /
JasperFx 2.27.0 / CW beta.3, and no new customer reports. ProductSupport has exactly **one** open
issue (#33, fixed by #3390).

Wolverine new-but-not-blocking: **#3380** (OpenAPI: route params bound only by compound-handler
`LoadAsync`/`Before` are missing from the operation — same class as the #3135 audit), **#3366**
(`UseAzureServiceBusTesting()` documented but test-suite-only), **#3365** (Polecat primary
`IEventStore` bridge registers twice). Polecat **#320** (`IEventStore.Subject` is the DB URI, so
primary + ancillary stores on one database are indistinguishable → CritterWatch HWM buckets collide)
is small and already has a pinned skipped test waiting.

Marten **#4920** (Guid `CompareTo` in LINQ, community) merged 07-12 — confirm whether it made 9.15.0
or is unreleased on master; it is the only candidate reason for a Marten patch this wave.

---

## Communications checklist

- [ ] Discord: 6.17.3 (draft ready, awaiting green publish) — then jfx 2.28.0 / 6.18.0 / CW beta.4
- [ ] ProductSupport#33 reply + close after 6.17.3 is resolvable
- [ ] **wolverine#3376: awaiting @erdtsieck's registration + connection breakdown — Track B is
      blocked on this**
- [ ] **wolverine#3388: awaiting @uniquelau's re-verify** — asked him to raise
      `TrackActivity().Timeout(...)` on his real monitored host. Keep open until he reports back.
- [ ] #3387/#3367 contributor: deferral message is JEREMY'S call — ask him, don't send
- [ ] CW #688 design exchange with erdtsieck; keep the #699 coalescing consistent with it
- [ ] erdtsieck beta.4 verification ask

## Conventions (unchanged, binding)

Full `wolverine.slnx` Release build before pushing; `--framework net9.0` for fast test iteration;
`Servers` for connection strings (compose Postgres = 5433); rebuild after every stash push/pop;
ImHashMap for hot-path lookups; private members camelCase; TCS-gated concurrency tests; version bump
before every publish (`--skip-duplicate` silently no-ops); `say` checkpoints; draft user-facing
wording and interview Jeremy at the end.

## Definition of done

- [x] Track A queue merged; Wolverine 6.17.3 shipped; GitHub release notes published
- [ ] Discord announcement + ProductSupport#33 closed
- [ ] #3376 direction resolved with the reporter (config bug vs architecture change)
- [ ] #3385 diagnostic shipped; characterization test flipped
- [ ] CW #697, #698 (both halves), #699+#636, #689 (closed), #610 (closed with evidence); beta.4 shipped
- [ ] All communications checklist items done

---

## ADDENDUM: CW #610 verification verdict (completed 2026-07-12, vs origin/main @ 63519193)

**Recommendation: CLOSE #610 with follow-ups.** The #694 (beta.3) read-side work answers the
substantive concerns — every shard state carries its own `(storeUri, tenantId, databaseIdentifier)`
and all gap math + action dispatch flows through that. Per-question:

| Q | Status |
|---|---|
| 1. Row attributed to store AND tenant | **RESOLVED** for Marten (`projections-store.ts:331`, `belongsToSubscription :273-279`, three-map sourcing `:335-357`). Polecat caveat: primary + ancillary sharing one DB are indistinguishable (`IEventStore.Subject` = database URI) — already filed upstream as **polecat#320** (pin test skipped in `src/SqlServerTests/polecat_ancillary_ieventstore_registration.cs`), plus **wolverine#3365** (double bridge registration → double poll). |
| 2. HWM/gap per-store-per-tenant | **RESOLVED** — HWM maps keyed `service → store → tenant` (`:958-971`) and per-database (`highWaterMarkForDatabase :643-645`); `hwmForShardState :567-584` picks tenant → own-database → store, never the cross-store last-writer mark. Minor edge: `perTenantGap :1675-1698` lacks a fallback store URI, only matters for pre-store-tagging satellites with the same tenant id on two partitioned stores. |
| 3. Grouping composes store × tenant | **PARTIAL** — flat view composes fully; the opt-in tenant-grouped view (#263 Phase 3c) has NO store axis: `PerTenantProjectionRow` lacks `storeUri` (`ProjectionsPage.vue:827-847`), rows bucket by projection base name (`:881-886`), `agentUri` = first name match across all stores (`:889`) → row fusion + possible wrong-store agentUri when two tenant-partitioned stores share tenant ids or projection names. |
| 4. Actions target correct (store, tenant) DB | **MOSTLY RESOLVED** — dispatch carries `(agentUri, tenantId)`; `AgentUriResolution.ResolveAsync` → `FindAgentUriAsync(shardIdentity, tenantId)` returns the per-tenant agent at the tenant's own DB; `RebuildScope.Resolve` rejects scope conflicts. Residual: `ShardIdentityFromLiveAgentUri` discards the store segment (`AgentUriResolution.cs:76-95`) and both resolver loops ask every family first-match-wins (`:105-118`; `RebuildProjectionHandler.cs:104-112`) → cross-store shard-identity collisions can resolve against the wrong store. jasperfx#502 registry-baseline gap on db-per-tenant also remains. |

**Coverage gap confirmed:** no test anywhere exercises ancillary stores AND db-per-tenant in the
same service (`projections-store-682-read-side.test.ts` is single-store; MultiTenancyTests all
single-store; MTTrips = db-per-tenant/single-store, MultiStoreHost = multi-store/no db-per-tenant).

**Follow-ups to file on close (smallest-first — fold 1-2 into beta.4, rest as issues):**
1. Combined-composition regression test: add a single-DB ancillary store to
   `projections-store-682-read-side.test.ts`; assert no cross-store mark borrowing. (S)
2. Store-scope the family loops: filter `IEventSubscriptionAgentFamily` by the store segment
   already in the live agent URI, in `AgentUriResolution.ResolveAsync` and
   `RebuildProjectionHandler.TryRebuildRegisteredProjectionAsync`. (S-M)
3. Store axis in the tenant-grouped view: `storeUri` on `PerTenantProjectionRow`, bucket by
   store × tenant, resolve agentUri from the owning store's ProjectionView. (M)
4. Sample + e2e for the composition: ancillary store in MTTrips (or db-per-tenant in
   MultiStoreHost) + one backend per-tenant-action test in that host. (M)
5. Link on close: polecat#320, wolverine#3365, jasperfx#502 (all already filed).
