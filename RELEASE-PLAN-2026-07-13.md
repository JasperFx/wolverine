# Release plan — 2026-07-13

Built from a 36-hour sweep (since 2026-07-11T12:00Z) of **Marten, Polecat, JasperFx, Weasel,
Wolverine, ProductSupport, CritterWatch**, filtered to still-open items.

**Already shipped today:** Wolverine **6.17.3** (9 PRs; live on nuget.org, release notes published).
**ProductSupport is empty** — zero open issues, zero open PRs.

Decisions taken with Jeremy: guard **throws** at startup; Marten **9.15.1 ASAP** for #4947 alone;
**#3376 = design now, build in 6.18.0**; **#3399 batches into 6.18.0** (no fast 6.17.4).

---

## Release 1 — Marten 9.15.1 (URGENT, ships first)

**Driver: #4947 — silent data-correctness regression, broken since 9.13.0.**

`ForTenant()` on an identity/dirty-tracked session stopped seeing tenancy-neutral (global)
documents. `LoadAsync` returns **null** for a document that is there. Reported working on 9.12.0,
broken on 9.15.0. Three releases of blast radius.

| Item | State |
|---|---|
| **#4947** | **PR #4948 OPEN.** Root cause: `74b11a461` (PR #4807, the fix for #4801) tenant-scoped the identity map **per session** instead of **per document type** — right for conjoined docs, wrong for tenancy-neutral ones. Fix gates sharing per type: identity-mapped AND not `Conjoined` AND same `Database` instance. 5 of 6 new tests fail on unmodified master; `Bug_4801` still passes; `DocumentDbTests` 1047/0. |

**Do not hold this for anything else.** Merge on green → bump → publish
(`on-manual-do-nuget-publish.yml --ref master`). Local-feed verification gate applies
(pack `-local.N`, verify Wolverine AND CritterWatch, then publish).

**Not in this patch:** #4946 (`BulkInsertEventsAsync` calls `ApplyAllConfiguredChangesToDatabaseAsync()`
on **every** call — 17 events/s vs >3,000/s in erdtsieck's 512-DB store). Accepted, he is PR-ing it;
rides the *next* Marten patch rather than gating a correctness fix.

**Confirmed not a driver:** #4920 (Guid `CompareTo` in LINQ) already shipped in **9.14.1**.

---

## Release 2 — Weasel 9.16.4 (contributor-driven, not urgent)

| Item | State |
|---|---|
| **#356** | erdtsieck. `db-apply` never releases each database's pool, so a 512-DB walk drags a tail of idle pools and then **failed a real production deploy today** on `53300`. Asked for pool-release + backoff-retry + `n/total` progress. **Accepted; he is PR-ing (1)+(2).** Note this IS implementable, unlike #3376's ask — a one-shot CLI owns its data sources. |

**Housekeeping (done):** #353 closed as superseded by the merged #354.
**Still to do:** publish a GitHub Release entry for **9.16.3** — the package is on NuGet but the
Releases page still says 9.16.2, which will mislead anyone checking versions.

---

## Release 3 — Wolverine 6.18.0 (the batch)

Nothing ships as 6.17.4; per Jeremy, everything batches here.

### Already merged, unreleased
| Item | What |
|---|---|
| **#3396** | `ApplyRestrictionsAsync` discarded the `StopRemoteAgent` that `EvaluateAssignmentsAsync` returned, so pausing an agent persisted a restriction and had **no immediate effect**. Plus the in-memory `Restrictions` was never refreshed, so paused *listeners* restarted themselves. Zero prior test coverage. |

### Open PRs
| Item | What |
|---|---|
| **#3400** | **GH-3388 guard.** Refuses managed distribution + an explicit Marten daemon (`MartenDaemonModeIsSolo()` / `AddAsyncDaemon(Solo\|HotCold)`) at host start. Two coordinators competing → a **hang**, not an error. Reverses the GH-3290 "never overwrite the user's choice" contract, because what it preserved was a deadlock. **It immediately caught five of our own TestHelpers fixtures** in exactly that state — which is why the #3388 cold path went unnoticed. MartenTests 518/518. |

### To build
| Item | Effort | What |
|---|---|---|
| **#3399** | S | Codegen emits an **invalid C# class name** for batched (`T[]`) message types when duplicate handler `TypeName`s are disambiguated → `ItemDeleted[]1177234954_...` → compile failure → **app dies at startup**. Only fires with `MultipleHandlerBehavior.Separated` + a class handling two types where one is batched, which is why existing batch tests pass. Fix ≈ sanitize the identifier. |
| **#3398** | M | `[AsParameters]`: an unparseable value in a **collection** query param silently binds `null` instead of 400. Finishes the #3372 job (which fixed only the scalar case). On a filter endpoint this **silently drops the predicate and returns an unfiltered 200** — FluentValidation can't compensate because it sees `null`. |
| **#3385** | S/M | gRPC header-identified saga: replace the opaque `StatusCode.Internal` with an actionable diagnostic; flip the characterization test. **@erikshafer greenlit for option (a)** and offered to take it. |
| **#3376** | **L** | **Owned-agent daemon scoping — the headline.** See below. |

### #3376 — what the reporter's answer changed

erdtsieck answered: **no `AddAsyncDaemon` anywhere near managed distribution.** So the cheap config
fix is off the table for his deployment. His measurements:

- 937 connections across 512 DBs / 2 nodes; **~808 are daemon `COMMIT`/`ROLLBACK` per-database work**, ~116 ordinary app traffic.
- **475–497 of 512 databases hold connections from BOTH nodes.**
- **A third node added ~350 connections while fully caught up and idle.** Adding capacity *increases* connection pressure.
- His `db-apply` step **failed for lack of connections** while the cluster was idle; scaling the API *down* fixed it.

Two constraints the design must respect (from recon, and confirmed by Jeremy):

1. **Pool release as originally specified is not implementable.** Marten's `NpgsqlDataSource` is owned
   by the tenancy's `MartenDatabase` and is **shared with ordinary application sessions** for that
   tenant on that node. Disposing it on agent revocation would abort live app connections.
2. **Command processing opens connections to any tenant regardless of daemon affinity.** So daemon
   scoping alone **cannot** reach `databases + overlap` — the app's own tenant traffic is a second,
   independent axis. Any honest target must say so.

**Revised direction to design (not pool release):** ownership-scoped *materialization* + true
**daemon quiesce**. Three real leaks already found, all worth fixing regardless:
- `EventStoreAgents._daemons` is **append-only** — daemons are never released.
- `JasperFxAsyncDaemon`'s per-database `System.Timers.Timer` starts in the **constructor** and is only
  stopped in `Dispose()` — `StopAllAsync` doesn't touch it.
- The daemon's subscription to the database's `ShardStateTracker` is **never disposed**; a rebuilt
  daemon re-subscribes and both observers stay attached.

**Next artifact:** post the revised design on #3376 (explicitly retracting the pool-release claim),
then build.

### Deferred
- **#3397** (adaptive connection budget) — erdtsieck **deferred it himself** behind #3376. Do not schedule.
- **#3387 / #3367** ([Entity] load profiles) — deferred by Jeremy. **The contributor still has not been told.**
- #3391, #3380, #3366, #3365, #3350, #3137, #3237 — no new signal.

---

## Release 4 — CritterWatch 1.0.0-beta.4 (pins Wolverine 6.18.0)

### Done since beta.3
#697 (PR #703), #698 CritterWatch half (PR #704), #701 (#689 docs half), #699 flooding half
(PR #705), #610 closed with evidence (residuals → #702).

### MUST
| Item | Effort | What |
|---|---|---|
| **#706** | M | **NEW, erdtsieck's beta.3 walkthrough.** DLQ fetch failures are **invisible on multi-DB/multi-tenant**: the pre-fanout enumeration is unguarded so one bad store aborts everything, and the per-store catches swallow-and-log — so a partial failure renders as **"No dead letter queue entries."** An operator cannot tell *empty* from *broken*. Same class #683 set out to kill. Needs a partial-failure result + "N of M databases failed to answer" banner. |
| **#699 retention** | M | PR #705 fixed only the flooding. `TimelineEntry` still has **no retention** — 671k rows, 65–85k/day for one small service. Reuse the #468/#695 partitioning/retention approach. |
| **#702** | M | The #610 residuals: `PerTenantProjectionRow` has no `storeUri` (rows fuse across stores); `ShardIdentityFromLiveAgentUri` discards the store segment (cross-store collisions resolve to the wrong store); **no test composes ancillary stores AND db-per-tenant**. |
| **#707** | S | NEW. Two nits: DLQ "Query Messages" disabled while the empty state tells you to click it; `GET /alerts/config/metrics/services/{name}` 404s for an unconfigured service. |

### Not beta.4
#689 second half (belt-and-braces only now — the real fix shipped in 6.17.2); #636 (remaining piece
is a fleet-driven E2E blocked on programmatic replica control); #632; #670; #347.
**#688** — owed a design sketch on the thread; no code gated on it.

### Blocked on the reporter
**#698 stays open.** Both halves are merged, but erdtsieck **has not re-verified**, and his
`wolverine_agent_restrictions` = 0 rows is **still unexplained** — the persist path is unconditional
and our cluster tests show pause working end to end. Asked him to check the **console's** store.

---

## Not releasing

- **JasperFx 2.28.0** — **hold.** #3376 was its only driver and the hook design it was going to carry
  turned out to be partly wrong. Nothing else in the sweep needs a JasperFx change.
- **Polecat** — no release. Zero activity in the window, zero open PRs. **#320** (`IEventStore.Subject`
  is the DB URI, so primary + ancillary on one database are indistinguishable → CritterWatch HWM
  buckets collide) is small and has a pinned skipped test; it rides the next Polecat release.

---

## Sequencing

```
Marten 9.15.1  (#4947)                    ── ship FIRST, alone, ASAP
Weasel 9.16.4  (#356, contributor)        ── independent; when his PR lands
Wolverine 6.18.0                          ── #3396 + #3400 + #3399 + #3398 + #3385 + #3376
        │
CritterWatch beta.4 (pins 6.18.0)         ── #706 + #699-retention + #702 + #707
```

## Communications

- [ ] **Discord**: 6.17.3 draft is written and waiting on Jeremy to paste. Then 9.15.1 / 6.18.0 / beta.4.
- [ ] **#3376**: post the revised design (retract pool-release; state the two-axis reality).
- [x] **#3385**: erikshafer greenlit for option (a).
- [x] **Marten #4946 / Weasel #356**: erdtsieck's contributions accepted.
- [x] **Weasel #353**: closed as superseded.
- [ ] **Weasel 9.16.3**: publish the missing GitHub Release entry.
- [ ] **#3387/#3367**: the contributor still has not been told it is deferred — **Jeremy's call**.
- [ ] **CW #698**: nudge erdtsieck to re-verify and to check the console's store.
- [ ] **CW #688**: owed a design sketch.
