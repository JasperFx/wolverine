# Release plan — wave of 2026-07-14

Scope: every **open community issue** plus **recently-opened maintainer issues** across JasperFx,
Weasel, Marten, Polecat, Wolverine, ProductSupport, CritterWatch, ai-skills.

ProductSupport and ai-skills have **zero open issues** — nothing to do there.

Wolverine #3376 is deliberately excluded pending a separate design discussion. #3397 is parked
behind it (its tuning baseline changes once #3376 lands).

---

## Status (live)

**Merged:** wolverine #3409 (ASB emulator) · CritterWatch #709 (DLQ partial failure), #711 (OpenAPI docs)
**In CI, no failures:** wolverine #3411, #3412, #3417, #3418, #3419 · CritterWatch #710, #712
**Changes requested (community):** wolverine #3407 (Kafka), #3410 (Cosmos F#)
**Awaiting erdtsieck's PRs:** weasel #356, marten #4946
**Running:** CritterWatch #702

### Issues filed during this wave

Work done here surfaced eight new issues, several more serious than what we set out to fix:

| Issue | Why it matters |
|-------|----------------|
| **wolverine #3408** | **Security-relevant.** Reserved envelope headers spoofable through the durable inbox. **Live on shipped code** via MassTransit interop — not merely reachable through the pending Kafka PR. Fixed in #3411 |
| **wolverine #3421** | The `AddOpenApi()` freeze is only *half*-closed on hybrid hosts by 6.17.2 — the document goes from "empty" to "Wolverine routes present, every minimal-API route missing", which is **strictly more deceptive** |
| **wolverine #3414** | Cosmos saga persistence has no optimistic concurrency — blind `UpsertItemAsync` silently loses updates under concurrent messages. Pre-existing, C# path |
| **wolverine #3415** | Every Cosmos saga lands in one logical partition → 20GB / 10k RU/s ceiling for the whole app's saga state |
| **wolverine #3416** | Cosmos requires a camelCase serializer policy and nothing says so; candidate for a bootstrap guard like #3400 |
| **wolverine #3413** | Test suites pollute each other through the shared `wolverine` schema. Hidden inside it: should the durability agent throw on an unresolvable transport, or dead-letter it? That's a real production shape |
| **wolverine #3420** | OpenAPI: Marten-aggregate-bound route ids render as `string`, not `uuid`, on unconstrained routes |
| **jasperfx #513** | `isNull x` codegen forces `[<AllowNullLiteral>]` on every F# saga, framework-wide |

### Corrections to my own issues

Three of my own diagnoses were **wrong**, and the investigations are worth more than the fixes:

- **#3365** — I guessed `IntegrateWithWolverine()` ran twice. It runs once. Polecat's `AddPolecat()` had *started* registering `IEventStore` and our bridge kept doing it too. A stale code comment asserted the exact precondition that had silently changed.
- **#3380** — the `parameters: []` symptom does not reproduce at all. The thesis was right, the aim was wrong; two *different* real defects found and fixed.
- **#3350** — the SNS per-tenant tests I fingered are 0.5s for 3 tests. The real hog is SQS. And Polecat's cost is **per-class, not per-test** — sharding by test duration would have skewed badly.

---

## Decisions taken (2026-07-14)

- **6.18.0 is held open** and the maintainer backlog folds into it. It is one bigger release, not
  6.18.0-now + 6.19.0-later.
- **#3366** → ship a real `UseAzureServiceBusEmulator()` API (not a docs-only fix).
- **#3350** → split the slow suites. Do not just raise `timeout-minutes`.
- **CritterWatch #77 / #74** (blue/green) → leave for now. Revisit after 1.0.

---

## Wave 1 — release mechanics that do not depend on 6.18.0 (today)

| Step | Detail |
|------|--------|
| 1.1 | Weasel: publish the missing **9.16.3** GitHub Release entry (package is live; page stops at 9.16.2) |
| 1.2 | Discord: 6.17.3 (drafted, unposted) and Marten 9.15.1 |

---

## Wave 2 — Wolverine 6.18.0 (held open; ships when everything below is in)

### Already merged to `main`

- #3396 — `ApplyRestrictionsAsync` never dispatched the commands it computed (fixes CritterWatch #698)
- #3400 — refuse a competing Marten daemon under managed distribution (GH-3388)
- #3401 — bump Marten to 9.15.1
- #3403 — actionable diagnostic for a header-identified saga over gRPC (GH-3385)
- #3404 — `[AsParameters]`: reject unparseable values in collection query parameters (GH-3398)
- #3406 — fix invalid generated class name for batched (array) message types (GH-3399) *(startup-fatal)*

### Still to build

Sequence **#3350 first** — it pays for itself across everything after it.

| Issue | Work |
|-------|------|
| **#3350** | **CI, do this first.** Split the AWS suite into parallel SNS/SQS jobs and shard CIPolecat, then re-enable **CIAWS** (currently commented out of the matrix in `.github/workflows/tests.yml`). Attack the wall-clock so the 20m cap stays a real signal rather than raising it into meaninglessness |
| **#3365** | Polecat primary `IEventStore` bridge registers twice — `GetServices<IEventStore>()` returns the same `DocumentStore` instance twice. Fix with `TryAddEnumerable`/dedupe. CritterWatch's `EventProgressionPoller` currently polls the primary store twice per pass |
| **#3366** | Promote a real `UseAzureServiceBusEmulator(...)` into `WolverineFx.AzureServiceBus` — sets `ManagementConnectionString`, standard emulator defaults, returns the config for chaining. Destructive delete-all-objects cleanup stays **opt-in**. Then rewrite `index.md`, `session-identifiers.md`, `conventional-routing.md` around the real API |
| **#3380** | OpenAPI: route parameters bound only by a compound-handler `LoadAsync`/`Before` are missing from the operation (`parameters: []`). Minimum bar: every `RoutePattern.Parameters` entry declared as a required path parameter, typed from whichever frame binds it. **Build the missing OpenAPI shape-test harness as part of this** — its absence is why this class of omission keeps shipping (same finding as the #3135 audit) |
| **#3391** | RabbitMQ: (a) regression test pinning the `ConnectionMonitor` tracking invariant across a callback-exception restart; (b) a successful eager restart of a listener never re-declares/`BasicConsumeAsync` — open channel, zero consumers, `State = Connected`. Revisit the #3137 quarantined circuit-breaker tests in the same pass |
| **#3408** | **Security-relevant, found reviewing #3407.** `EnvelopeSerializer.writeHeaders` appends every `env.Headers` entry unfiltered *after* the typed props, and the reader parses reserved keys back into typed properties — so a `Headers` entry under a reserved key (`tenant-id`, `saga-id`, `id`, `message-type`) **silently overwrites the real property** on any durable round trip. Inert in memory, live the moment it crosses the inbox/outbox. Filter reserved keys (skip, don't throw). Prerequisite for taking #3407 |
| **#3407** | Merge the Kafka community PR — **changes requested**, gated on #3408 (see Wave 3) |
| **#3410** | Review + merge the Cosmos F# saga codegen community PR (thechucklingatom, see Wave 3) |

### Ship

1. Bump `Directory.Build.props` → `<Version>6.18.0</Version>`
2. `dotnet build wolverine.slnx -c Release` (FULL solution, not `_slim`)
3. Tag `V6.18.0`, run `publish_nugets.yml`, poll the nuget flat container
4. GitHub release notes calling out every issue + PR
5. Discord announcement
6. Ask erdtsieck to re-verify **CritterWatch #698** on 6.18.0, then close it

---

## Wave 3 — community PRs (gated on contributors, 1–3 days)

We are not writing this code. We are reviewing and shipping it.

### 3.1 Weasel #356 — `db-apply` connection discipline → **Weasel 9.16.4**
erdtsieck PR-ing. Accepted asks: release each database's `NpgsqlDataSource` after its apply;
bounded backoff-retry on `53300`; per-database `n/total` progress logging.

Cleanly implementable *because* `db-apply` is a one-shot CLI that owns its data sources — the exact
property the daemon side lacks (which is why #3376 is hard).

### 3.2 Marten #4946 — `BulkInsertEventsAsync` schema apply per batch → **Marten 9.15.2**
erdtsieck PR-ing. The batch overload opens with an unconditional
`ApplyAllConfiguredChangesToDatabaseAsync()`; the streaming overload has no such call. Direction
given: make it **once-per-database**, not per-call (floor: skip entirely under `AutoCreate.None`).

Not a 9.15.0 regression — rides the patch, does not gate it.

### 3.3 Wolverine #3407 — Kafka timestamps + record headers (jakub-petrylak-onerail)
**Reviewed 2026-07-14 — changes requested.** Direction is right (a raw-JSON listener has no Wolverine
metadata on the wire, so the record timestamp and record headers are the only source), and it is
correctly scoped to `JsonOnlyMapper`, leaving the Wolverine↔Wolverine path alone. Two findings:

1. **Blocking — unfiltered header copy.** Kafka header keys are chosen by the producer, which on a
   raw-JSON topic is by definition not Wolverine and not necessarily trusted. The copy loop does not
   exclude `EnvelopeConstants` reserved names, so an external `tenant-id` / `saga-id` / `id` lands in
   `envelope.Headers` and then gets promoted into the typed property on the first durable round trip
   (see **#3408**). The PR's own example header is `tenant-id` — the trap in miniature.
2. **Vacuous test.** `stamps_envelope_sent_at_from_the_kafka_record_timestamp` asserts
   `SentAt > now - 5min`, but `Envelope.SentAt` is initialized to `DateTimeOffset.UtcNow` at
   construction (`Envelope.cs:248`) — it passes on `main` today, with or without the change. Needs a
   raw producer writing an explicit past `CreateTime` and an exact-equality assertion.

Ships **in Wolverine 6.18.0** (Wave 2), after #3408 lands.

### 3.4 Wolverine #3410 — Cosmos F# saga codegen (thechucklingatom)
Adds `CosmosDbPersistenceFrameProvider` saga frames + a new `LoadDocumentFrame`, with an F# sample and
a checked-in `Generated.fs`. Under review. Things that decide it: whether the Cosmos point-read supplies
a correct **partition key** for saga identity (wrong = cross-partition query or a silent miss), whether
it diverges from the Marten/EF Core/RavenDb `IPersistenceFrameProvider` shape (`MarkCompleted` → delete,
optimistic concurrency), and whether the F#/Cosmos test projects are in `wolverine.slnx` at all — if they
aren't, the PR has no CI coverage, which is its own finding.

Ships **in Wolverine 6.18.0** (Wave 2) if it holds up.

**Pin cascade:** Weasel 9.16.4 → Marten 9.15.2 → Wolverine + CritterWatch pin bumps. If Marten
9.15.2 lands before 6.18.0 ships, fold its pin bump into 6.18.0 rather than cutting a follow-up.

---

## Wave 4 — CritterWatch beta.4

Pins Wolverine 6.18.0.

| Issue | Priority | Work |
|-------|----------|------|
| **#706** | **MUST** | DLQ fetch failures are invisible on multi-database/multi-tenant persistence: `dlq-operation` throws ~1/hr yet the page renders "No dead letter queue entries" with no partial-failure indication. A silent-empty on a partial failure is the worst possible rendering |
| **#699** | HIGH | Retention half of the "Agent started" flood (671k rows, 3.5k/hr). Emit half already fixed (CW #705) — this is the retention/compaction policy for the existing rows |
| **#707** | MED | beta.3 nits: DLQ "Query Messages" disabled contradicts its own empty state; metrics-overrides GET 404s for unconfigured services (console noise) |
| **#702** | MED | Store-axis gaps in the tenant-grouped view + agent-URI resolution (follow-ups from the #610 audit) |
| **#689** | LOW | Docs/release note: monitored hosts using `AddOpenApi()` need the wolverine#3371 fix |
| **#698** | — | Close after erdtsieck re-verifies on 6.18.0 |

---

## Wave 5 — design track (no code yet)

These need a decision or a written design before they can be scheduled.

- **wolverine #3376** — daemon connection scoping. *Separate discussion with Jeremy.* Known blockers:
  Marten's `NpgsqlDataSource` is owned by the tenancy's `MartenDatabase` and shared with app sessions,
  and command processing opens connections to any tenant regardless of daemon affinity — so daemon
  scoping alone cannot reach the target. Three leaks already identified:
  `EventStoreAgents._daemons` is append-only; `JasperFxAsyncDaemon`'s per-DB `System.Timers.Timer`
  starts in the ctor and only stops in `Dispose()`; the `ShardStateTracker` subscription is never disposed.
- **wolverine #3397** — adaptive connection budget. Parked behind #3376 (erdtsieck himself said the
  tuning baseline changes once owned-agent scoping lands). Post that as the holding position.
- **CritterWatch #688** — per-tenant projection views at hundreds of tenants. Needs decomposition into
  roll-up rows / problem-tenants filter / tenant pivot / alert coalescing before it is schedulable.
- **CritterWatch #77 + #74** — erdtsieck's blue/green deployment asks. **Decided 2026-07-14: leave for
  now**, revisit after 1.0. The wave stays focused on the live 512-tenant production issues.

---

## Explicitly out of scope this wave

Not gaps — deliberate omissions.

- CritterWatch's ~35 June-2026 epics (Event Modeling, Workflow Visualization, Explorer IA, …)
- Marten #4685 / #4682 / #4684 — the rebuild-perf epic (two draft PRs already open)
- JasperFx #480 / #459 / #435 / #430
- Polecat #180, #318, #320 — real, but they trail the Wolverine/Marten work they depend on
- Marten #4944 — sharded-tenancy `pg_inherits` partition sweep
- JasperFx #503 — tenant-scoped `IEventStore` explorer read overloads

(#318, #320, #4944 and #503 are all mine and recent; they're deferred rather than dropped — they
belong to the CritterWatch multi-store/multi-tenant explorer arc, which is post-beta.4.)
