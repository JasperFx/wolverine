# Next-wave release plan — post-sweep follow-through (2026-07-12 evening state)

> **SUPERSEDED (2026-07-12 ~23:30 UTC)** by `NEXT-WAVE-HANDOFF.md`, which reflects the mid-wave
> execution state (Track 1 queue nearly complete, PRs #3389/#3390/CW#701 open, #3387 deferred,
> ProductSupport#33 + CritterWatch unblocked-now items folded in). Hand THAT file to the agent;
> keep this one for background rationale.

Successor to `ERDTSIECK-EPIC-PLAN.md` (that epic is COMPLETE — see "What landed" below).
Scope: the open community PR queue, the #3376 implementation, three new CritterWatch issues
from @erdtsieck, and the release train that ships all of it.

---

## What landed (previous wave — all same-day 2026-07-12)

**Releases shipped:** JasperFx **2.27.0** (V2.27.0), Marten **9.15.0**, Wolverine **6.17.2**
(pins already bumped via #3383), CritterWatch **1.0.0-beta.3** (pins via #696).

**Issues fixed & closed (15):**

| Repo | Closed | Via |
|---|---|---|
| jasperfx | #505, #506, #507 | PR #508 (evolver cast), PR #509 (Block observability + fault semantics + narrowed teardown catches) |
| marten | #4941, #4942, #4943 | PR #4945 (idempotent provisioning repair on auto-assign path); #4941/#4943 closed by combination/reporter |
| wolverine | #3371, #3372, #3374, #3368 | PR #3373 (uniquelau, HttpGraph provider), #3379 (opt-in strict query binding), #3381 (binding frames once per chain), #3382+#3369 (gRPC tenant detection + envelope propagation) |
| CritterWatch | #682–#687 | PRs #690–#695, #700 (wording), shipped in beta.3 |

**Issue-closure audit — nothing to close manually right now.** Every remaining open issue is
legitimately open, and four of them close automatically when their in-flight PR merges:

| Open issue | Closes via | State |
|---|---|---|
| wolverine#3375 | **PR #3384** (erdtsieck's sweeper — "Closes #3375") | PR open, needs review |
| wolverine#3367 | **PR #3387** (outofrange-consulting's load-profile POC) | PR open, needs decision+review |
| wolverine#3332 (CIAWS timeout) | **PR #3364** (Steve-XYZ — "Fixes #3332") | PR open, needs review |
| jasperfx#510 (new, uniquelau) | **PR #511** (uniquelau — "Closes #510") | PR open, needs review |
| wolverine#3376 | design note posted by maintainer; implementation is THIS wave's headline | open |
| wolverine#3385 (new, erikshafer) | direction decision needed; test PR #3386 pins the gap | open |
| CritterWatch #688 | design epic (per-tenant at scale) — ongoing | open |
| CritterWatch #697/#698/#699 (new, erdtsieck) | this wave, beta.4 | open |

---

## New inbound since the sweep

### erdtsieck round 2 — CritterWatch operator-trust issues (filed ~16:00–17:00, vs beta.2)

All three were found on **beta.2** — first triage step: confirm none were incidentally fixed in
beta.3 (they weren't in its PR list, but verify behavior before coding), and ask him to upgrade
to beta.3 + Wolverine 6.17.2 so round-2 fixes are verified on current bits.

- **[#697](https://github.com/JasperFx/CritterWatch/issues/697)** — Acknowledge/Snooze alert writes
  no audit entry and records no actor; `ClearAlert` does both. Fix: mirror the ClearAlert pattern in
  `AlertCommandHandler` (`AcknowledgedBy`/`SnoozedBy` from envelope principal with UI fallback, on
  the events + `AlertRecord`, plus `auditLog.LogAsync`). Also `/audit` → `/audit-log` route alias
  (same class as the `/dead-letters`→`/dlq` alias from #693). **S**
- **[#698](https://github.com/JasperFx/CritterWatch/issues/698)** — Pause projection acks Succeeded
  (+audit +lifecycle event) **but the agent never pauses**; `wolverine_agent_restrictions` has 0 rows.
  Two-part:
  1. **Wolverine-side investigation (do first — possibly an upstream bug):** does
     `IAgentRuntime.ApplyRestrictionsAsync` actually persist a restriction row for
     event-subscription agents on 6.17.x, and does the leader-forwarding path
     (#478 `LeaderExecution.ShouldExecuteHereAsync`) execute it on a node that can write the
     restriction store? Reproduce with a 2-node cluster + Marten ancillary store agent. If broken
     upstream, that's a Wolverine issue + fix in this wave's Wolverine release.
  2. **CritterWatch-side regardless of root cause:** `PauseProjectionHandler` must verify observed
     reality (restriction row present, or agent actually stopped) before acking Succeeded — a green
     ack + audit trail for a no-op is worse than a failure. **M overall**
- **[#699](https://github.com/JasperFx/CritterWatch/issues/699)** — Timeline flooded by keep-alive
  agent re-reports rendered as "Agent started" (3.5k/hour, 671k rows, no retention). Fix: the
  console materializes a timeline entry only on **(agent → node) assignment change** (ServiceSummary
  knows the previous state); dedupe identical consecutive entries; add retention for
  `TimelineEntry` docs (same growth-pressure class as #468 metrics samples — reuse that approach).
  Keep the keep-alive as state refresh, it's correct for health. **M**

### Other new community items

- **jasperfx#510 / PR #511** (uniquelau) — `RunCommand` double-starts a host already started under
  `JasperFxEnvironment.AutoStartHost` (Alba/WebApplicationFactory harnesses), re-running every
  `IHostedService.StartAsync`. Repro against 2.27.0 included. Review checklist: fix must skip
  `StartAsync` only for the pre-started `PreBuiltHostBuilder` path, not change cold `run` semantics;
  needs a regression test with a counting `IHostedService`.
- **wolverine#3385** (erikshafer) — gRPC can't start/continue **header-identified** sagas
  (`saga-id` never crosses the hop: interceptors don't carry it, `Executor.InvokeAsync<T>` doesn't
  seed `envelope.SagaId` from context). Message-body-identified sagas already work (proven by his
  test PR #3386). Decision per his own framing: **ship the scoped first cut** — clear diagnostic
  instead of opaque `IndeterminateSagaStateIdException`, document body-identity as the supported
  path; defer full three-point propagation until someone shows a concrete header-identity need.

---

## The wave itself

### Track 1 — Wolverine PR review queue → ship as 6.17.3 (patch, fast)

**Added scope (2026-07-12 evening):**
- **[ProductSupport#33](https://github.com/JasperFx/ProductSupport/issues/33)** — CritterWatch
  telemetry caught by tracked-session waits. Fix (branch `trackedsession-ignore-telemetry`):
  TrackedSession's default ignore rule extended from `IAgentCommand` to all `INotToBeRouted`
  (covers `ICritterWatchMessage` telemetry) with an explicit carve-out for `Acknowledgement` /
  `FailureAcknowledgement`, which the session's ack APIs depend on. Ships in **6.17.3**. Also
  document `IgnoreMessagesMatchingType` as the workaround for older Wolverine versions in the
  testing docs, and reply/close on ProductSupport#33 once released.
- **Sweeper follow-up from the #3384 post-merge review** — one-line unregistration-race fix
  (`TryRemove` with exact KVP instance in `PersistenceMetricsSweeper`) + `UpdateMetricsPeriod`
  zero-guard. Fold into 6.17.3.

Bug/scale fixes only; erdtsieck's production benefits immediately. Review in this order:

1. **PR #3384** (erdtsieck — metrics sweeper, closes #3375). Review focus: `DurabilityAgent`
   register/unregister lifecycle vs node shutdown races; the dynamic re-read of the registration
   set (he explicitly built it to compose with #3376's owned-agent scoping — verify that claim);
   at-most-one-in-flight concurrency test quality; CosmosDb/RavenDb single-store agents correctly
   left on `StartPolling`. Confirm OTel gauge tags and `PersistedCounts` feed are byte-identical
   (CritterWatch depends on them).
2. **PR #3370** (kconfesor — RabbitMQ listener ghosting after broker restart). Directly adjacent to
   the #3171/#3187 channel-only-shutdown work — review against that: removing `_monitor.Remove(this)`
   must not reintroduce the latched-Disconnected state #3187 fixed; the agent must stay tracked so
   `connectionOnRecoverySucceededAsync` rebuilds it. Ask for/verify a test in the compliance-test
   style; be mindful the RabbitMQ suite has known shared-broker flakes.
3. **PR #3364** (Steve-XYZ — SNS per-tenant LocalStack fix, re-enables CIAWS, fixes #3332).
   Low-risk test-only change; the authoritative validation is the CI run itself. Merging also
   partially addresses #3350 (leave #3350 open for the CIPolecat half).
4. **PR #3386** (erikshafer — gRPC saga coverage, test-only, refs #3385). Merge as no-regret
   groundwork; the characterization test's assertions flip when the #3385 diagnostic lands.

Merge gate for all: green CI watched manually (`gh pr checks`), **never `--auto`**. Then ship
**Wolverine 6.17.3** (bump version, `publish_nugets.yml` + `V6.17.3` tag).

### Track 2 — JasperFx 2.28.0

1. **PR #511** (uniquelau) — review + merge (closes #510).
2. **#3376 companion: per-database daemon lifecycle hooks.** Implement the JasperFx.Events side of
   the design note posted on wolverine#3376: start/stop hooks for per-database daemon
   infrastructure (HighWaterAgent lifecycle) that Wolverine's distribution layer can drive on agent
   assignment/revocation. Follow the design note on the issue; erdtsieck was invited to comment —
   check the issue thread for his feedback before finalizing the hook shape.
3. Ship **JasperFx 2.28.0** — AUTOMATIC publish on green merge (policy carried over):
   `gh workflow run on-manual-do-nuget-publish.yml --repo JasperFx/jasperfx --ref main`, bump
   `<Version>` first, poll the flat container after.

### Track 3 — Wolverine 6.18.0 (the headline release)

1. **#3376 implementation** — owned-agent connection scoping on the Wolverine side, consuming the
   2.28.0 hooks: managed distribution decides which databases a node materializes daemon infra for;
   assignment starts it, revocation stops it and releases pools; grace overlap during rebalancing.
   Target: steady-state connections ≈ `databases + small overlap`, not `nodes × databases`.
   Multi-node tests per the original plan (assert zero pools for unowned databases post-rebalance;
   TCS-gated, no sleeps). Composes with the #3384 sweeper's dynamic set — add a joint test.
2. ~~PR #3387 ([Entity] load profiles)~~ — **DEFERRED per Jeremy (2026-07-12): not taking this in
   at this time.** Leave the PR and #3367 open; no review/merge work this wave. Revisit later.
3. **#3385 scoped diagnostic** — replace the opaque exception with a clear "header-identified saga
   over gRPC is not supported; put the id on the message body" diagnostic + docs note; flip the
   #3386 characterization test. NOTE: the gRPC + Sagas docs page (PR #3389, this wave) already
   documents the limitation — the diagnostic work should link to it.
4. **#698 upstream half** — if Track 4's investigation shows `ApplyRestrictionsAsync` doesn't
   persist restrictions for event-subscription agents (or leader-forwarding writes to the wrong
   store), fix here.
5. Marten/JasperFx pin bumps to 2.28.0 (+ Marten if it releases). Full
   `dotnet build wolverine.slnx -c Release` before pushing, as always.
6. Ship **Wolverine 6.18.0**.

### Track 4 — CritterWatch beta.4

**Unblocked-now additions (2026-07-12 sweep of the non-1.1 backlog; 1.1-milestone items and
post-1.0-labeled epics deliberately untouched):**

- **[#689](https://github.com/JasperFx/CritterWatch/issues/689)** — UNBLOCKED: Wolverine 6.17.2
  shipped the #3371/#3373 OpenAPI fix. Write the upgrade-notes/compat entry (monitored hosts using
  `AddOpenApi()` should be on Wolverine ≥ 6.17.2), and evaluate the issue's second ask — deferring
  the capability snapshot's ApiExplorer read until after app start so CritterWatch isn't the
  trigger on unfixed hosts. Small; do now, close with beta.4.
- **[#610](https://github.com/JasperFx/CritterWatch/issues/610)** — likely LARGELY RESOLVED by the
  #682/#694 dimension-aware read-side work (which was driven by exactly the composition #610 worries
  about: db-per-tenant main store + ancillary stores). Verification in progress; expected outcome is
  close-with-evidence plus at most a small follow-up (the (store × tenant) action-targeting check and
  the missing combined-axes sample/test noted in the issue).
- **[#636](https://github.com/JasperFx/CritterWatch/issues/636)** — Recent Events widget live-feed
  defects: bundle with #699 (same timeline/event-feed surface; the assignment-change-only
  materialization from #699 directly changes what this widget shows). Do together in beta.4.
- **Candidates, not committed** (pull in if beta.4 has room): #632 (explorer navigability quick
  wins — breadcrumbs/deep-links; parent epic #631 stays post-1.0), #670 (sequence-diagram
  click-through — check whether the #687 renderer rework in beta.3 changed feasibility), and the
  manual-test walkthrough docs (#347 is marked PRIORITY; pure writing, no code dependency).

Order: #698 investigation first (it may feed Track 3), then fixes.

1. **#698** — reproduce pause-no-op against beta.3 + 6.17.2 on a 2-node cluster. Split the fix:
   verify-before-ack in `PauseProjectionHandler` (CritterWatch, always) + whatever Track 3 item 4
   finds (Wolverine, maybe). The reporter's environment is "freely pokeable" — take him up on it.
2. **#697** — actor + audit entries for Acknowledge/Snooze, `/audit` route alias.
3. **#699** — assignment-change-only timeline materialization + consecutive dedupe + retention
   policy for `TimelineEntry` (reuse the #468/#695 partitioning/retention approach — and check the
   beta.1-drift lesson from #685: any new table shape must handle upgraded stores loudly).
4. **#688 design** — continue the per-tenant-at-scale design exchange with erdtsieck; no code
   gated on it for beta.4, but the #699 roll-up thinking (dedupe, coalesce) should be consistent
   with the #688 direction (alert coalescing).
5. Pin bumps (Wolverine 6.18.0, JasperFx 2.28.0, Marten if released) → ship **beta.4**; ask
   erdtsieck to re-verify #697/#698/#699 plus the round-1 fixes on his environment.

### Marten this wave

No open community issues. Work item: consume JasperFx 2.28.0 (daemon lifecycle hooks may require
daemon-hosting adaptation in Marten — assess once the hook shape is final). If a Marten release is
needed, the **local NuGet verification gate applies unchanged**: pack `-local.N` →
verify Wolverine AND CritterWatch against the local feed → only then publish
(`on-manual-do-nuget-publish.yml --ref master`). Worktrees only, never the shared tree.

---

## Sequencing

```
Track 1  PR queue reviews (#3384, #3370, #3364, #3386) ──► Wolverine 6.17.3  (independent, do first)
Track 2  jasperfx PR #511 + #3376 lifecycle hooks ──► JasperFx 2.28.0 (auto-publish)
                                                          │
Track 3  #3376 impl + PR #3387 + #3385 diagnostic ────────┴──► Wolverine 6.18.0
              ▲                                                    │
Track 4  #698 investigation ──(upstream half, if any)──┘           │
         #697/#699 fixes ──────────────────────────────► CW beta.4 (pins 6.18.0/2.28.0)
Marten   consume 2.28.0; release only if needed (local-feed gate: Wolverine + CritterWatch)
```

Track 1 has zero dependencies — start immediately; it also de-risks erdtsieck's production while
Tracks 2/3 build. The #698 investigation should start early since its outcome shapes Track 3.

## Working conventions

Unchanged from `ERDTSIECK-EPIC-PLAN.md` — full-solution builds, worktrees for Marten, manual
merge-on-green (never `--auto`), version bump before every publish, `--skip-duplicate` no-op trap,
nuget index-lag polling, TCS-gated concurrency tests, `ImHashMap` on hot paths, `Servers` for
connection strings, `say` checkpoints, wording interview with Jeremy for user-facing text.

## Definition of done

- [ ] PRs #3384, #3370, #3364, #3386 reviewed + merged; issues #3375, #3332 auto-closed
- [ ] Wolverine 6.17.3 shipped and resolvable
- [ ] jasperfx PR #511 merged (#510 closed); #3376 lifecycle hooks merged; JasperFx 2.28.0 auto-published
- [ ] wolverine #3376 implemented + multi-node tests green; joint test with the #3384 sweeper
- [ ] PR #3387 reviewed + merged (#3367 closed); docs page for load profiles
- [ ] #3385 scoped diagnostic merged; #3386 characterization test flipped; docs updated
- [ ] #698 root cause identified (Wolverine vs CritterWatch or both); fixes merged on the right side(s); verify-before-ack in place
- [ ] Wolverine 6.18.0 shipped (pins at 2.28.0/latest Marten); full wolverine.slnx Release build green
- [ ] CritterWatch #697, #698, #699 fixed + tested; beta.4 shipped with bumped pins
- [ ] Marten: 2.28.0 consumption assessed; if released, local-feed gate passed first
- [ ] erdtsieck replied to on #697/#698/#699 + asked to upgrade to beta.3/6.17.2 baseline, then re-verify on beta.4/6.18.0
- [ ] #688 design exchange has a concrete next artifact (wireframe/design note), consistent with the #699 coalescing work
