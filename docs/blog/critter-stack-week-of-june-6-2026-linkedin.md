# A Big Week for the Critter Stack: Per-Tenant Event Partitioning, F# Codegen, and 33 Releases

What happens when a .NET stack ships **33 NuGet releases in 7 days**, lands two major new feature tracks across five repos, and absorbs 17+ community bug reports — all without breaking GA?

That was the past week in the JasperFx organization (Marten, Wolverine, Polecat, JasperFx, Weasel, CritterWatch). Here's a tour for .NET developers building event-sourced, multi-tenant, or high-throughput systems.

---

## 📦 The Release Wave (May 30 → June 6)

**Marten**: 11 releases (V9.3.4 → V9.6.0, plus an 8.37.2 backport)
**Wolverine**: 8 releases (V6.3.0 → V6.5.0)
**JasperFx**: 6 releases (V2.2.4 → V2.8.1)
**Polecat**: 2 releases (V4.2.0, V4.2.1)
**Weasel**: 1 release (V9.0.3)

Most of those are coordinated patch waves. The big version bumps (Marten 9.4.0, Wolverine 6.3.0/6.5.0, JasperFx 2.5.0) each anchor a major feature track.

---

## 🎯 The Big Tracks

### 🆕 Per-Tenant Event Partitioning

The headline feature of the week. In Marten 9.4.0+, opt in with one line:

```csharp
opts.Events.UseTenantPartitionedEvents = true;
```

What that unlocks:

- **Native PostgreSQL LIST partitioning** of `mt_events` and `mt_streams` by `tenant_id`
- A **per-tenant event sequence** (`mt_events_sequence_{suffix}`)
- `mt_event_progression` keyed by `(name, tenant_id)` via a folded `{Name}:{ShardKey}:{tenantId}` shard grammar (no new column needed)
- **Vectorized per-tenant high-water detection** — one round-trip per database emits a high-water vector for every active tenant
- Per-tenant rebuild isolation + cross-tenant rebuild fan-out
- Composite single-pass rebuild executor (read-once / fan-out)

Constraints (validated at `DocumentStore` construction):
- Requires conjoined tenancy
- `EventAppendMode.Quick` or `QuickWithServerTimestamps` only (Rich is out of scope)
- Can't currently combine with `UseArchivedStreamPartitioning` (planned follow-up)

The flag defaults to `false`; existing stores keep the global append path byte-for-byte.

Wolverine 6.5.0 followed through with a full handler-side matrix: `[ReadAggregate]` / `[WriteAggregate]`, required-write isolation, optimistic versioning, cascading tenant inheritance, `MartenOps` tenant overloads, `AlwaysEnforceConsistency`, natural keys, multi-node distribution, ancillary stores, and the `IEventStream<T>` compound-handler append path.

Polecat (the SQL Server-backed sibling) started its parallel track immediately.

This is the kind of feature that removes a real scalability ceiling — the single shared event store stops being the bottleneck across tenants. If you're running an event-sourced multi-tenant SaaS on PostgreSQL, this is the upgrade to plan for.

📖 Per-Tenant Event Partitioning docs: https://martendb.io/events/multitenancy#per-tenant-event-partitioning

### 🆕 F# Pre-Generated Code

Wolverine 6.3.0 added a one-line CLI command that turns your handlers into runnable F# source files you can check into your repo:

```bash
dotnet run -- codegen write --language fsharp
```

The end-to-end slices that ship as compile-gated runnable samples:

- EF Core handlers
- Marten document handlers
- Marten event-sourced aggregate handlers
- FluentValidation + CosmosDB
- HTTP routing (JSON, route binding, query binding — both string and typed)
- In-memory sagas
- Behavioural Static-mode run-step

This positions Wolverine as a first-class F# option — not just "C# that happens to compile" but actual idiomatic F# emission from the code generator. There's a tutorial at https://wolverinefx.io/tutorials/fsharp.html and an open community-input discussion on idiomatic F# handler discovery (module-level functions, DUs, records) that would benefit from real F# adopter input.

### 🆕 JasperFx.Aspire — CLI Verbs as Dashboard Commands

If you're using .NET Aspire for your dev loop, JasperFx CLI verbs (`describe`, `codegen`, `openapi`, `wolverine-diagnostics describe-handlers`, etc.) are now exposed as **Aspire dashboard commands** with **startup gates**. Click a button instead of hopping to a terminal.

Pairs with the existing build-time `openapi` command and the source-generated discovery work that shipped the prior week.

### 🆕 Inline Request/Reply Over Wolverine.Http and Wolverine.Grpc

`InvokeAsync<T>` now flows efficiently over Wolverine's own HTTP and gRPC sender transports. Combined with the `Result<T>` work that shipped the week before, this rounds out the RPC-style story for Wolverine — you can do remote request/reply in the idiomatic Wolverine way without dropping down to bare HttpClient/Grpc.

### 🐛 Critical Reliability Fixes Shipped

Highlights worth flagging if you're on the GA majors:

- **DCB tag-concurrency race** — concurrent DCB tag-boundary appends sharing a tag could both commit. Fixed by serializing through a side table.
- **`OnException` middleware now cascades return-value messages** — a long-standing UX gap.
- **Concurrent health-check race** that corrupted leadership locks under sustained load.
- **Codegen scope priming** for service-located `MessageContext` / `IDocumentSession` — closes the family of duplicate-variable codegen bugs that hit several adopters at GA.
- **`BatchedQuery.FetchForExclusiveWriting` race** that wedged Wolverine HTTP `[Aggregate(LoadStyle = Exclusive)]` endpoints under concurrency.
- **`SecondaryStoreProxyFactory` regression in 9.5.1** — first concurrent build threw "Duplicate type name within an assembly". Fixed in 9.5.2 hours after report.
- **Projection rebuild stress-test fixes**: write-path `*Projected` variants and read-path `LoadProjectedAsync` now bypass session-shared trackers, and `ProjectionDocumentSession` routes user-code `LoadAsync` through a projection-safe path. Closes thread-safety side effects of the 9.0 changes.

### 🆕 Marten.ScaleTesting CLI

A new performance/scale harness landed: an event seeder, a `TelehealthComposite 4+2+2` scenario, plus `rebuild` / `validate` / `stress` subcommands. This is becoming the official rebuild-correctness stress vehicle. Signals a near-term focus on rebuild correctness and projection performance at production scale.

---

## 🌟 The Community Story

The thing I find most encouraging about this stack right now is **how much real-world feedback is flowing through the issue tracker**.

This week alone:

- **12 community issue reporters** filed real, reproducible bugs
- **2 first-time PR contributors** landed merges (@steve-ziegler reported AND fixed a race in `BatchedQuery.FetchForExclusiveWriting<T>` in their first PR; @midub fixed a NATS JetStream scheduled-send bug)
- **3 other community PRs merged** from established contributors

A few standouts:

**@erdtsieck** continues to be the de facto QA partner for advanced Marten paths — filed four high-quality per-tenant-partitioning bug reports surfacing real adopter edge cases (sharded provisioning, `StartStream` semantics, optimistic-append compatibility, `BulkInsertEventsAsync` NULL types). Every one was real, reproducible, and closed within the week.

**@RorySan** found and **fixed** an `IndexOutOfRangeException` regression on tenant-mapped projection documents in their first contribution, then immediately found a second related regression (9.5.1 → 9.5.2 patch cycle).

**@dmytro-pryvedeniuk** caught a concurrent health-check race that could corrupt leadership locks under sustained load — exactly the kind of failure that only shows up in production-shaped workloads.

The patch cadence on community-reported bugs is typically **sub-24-hour** for high-severity issues. That tight loop is the real story.

---

## 📈 Patterns Worth Watching

1. **The per-tenant partitioning rollout was a model multi-repo execution.** Umbrella issue → top-of-chain JasperFx surface → Marten implementation → Wolverine handler matrix → Polecat parallel track → community-reported edge cases → cascading patch wave. All inside a week.

2. **Patch cadence is sub-12-hour for severe bugs.** A 9.5.1 regression was fixed in 9.5.2 within hours of being reported. The GA `messageContext` codegen regression cluster from late May is now entirely closed.

3. **Aspire is becoming the canonical dev-time UI** for the Critter Stack. `JasperFx.Aspire` brings CLI verbs into the dashboard, the docs site picked up an Aspire integration page, and the friction to debug/inspect a Critter Stack app at dev time has dropped significantly.

4. **Marten.ScaleTesting CLI** signals a near-term focus on rebuild correctness and projection performance at production scale. Expect benchmark-driven changes in the next month.

5. **Three open community Discussions** are worth a triage pass — particularly one asking about the Marten release cadence and another asking what replaced `ProjectEventAsync` in Marten 9 (almost certainly a migration-docs gap).

---

## If You're Adopting

Safe upgrade matrix as of this morning:

- **Marten 9.6.0** + **Marten.AspNetCore 9.6.0**
- **Wolverine 6.5.0**
- **Polecat 4.2.1**
- **JasperFx 2.8.1** family (RuntimeCompiler stays on its 5.x line)

Two semantic changes from Marten 8 → 9 that are worth knowing before you upgrade:

1. The default `IDocumentSession` resolved from DI is now a **lightweight session** (no identity map, no dirty tracking). If your code depends on identity-map semantics, opt in explicitly.
2. Marten 9 uses **source-generated projection dispatch**. Your self-aggregating types compile out of the box now, but `partial` is still required on `*Projection` subclasses.

The 5 → 6 migration on Wolverine covers `ServiceLocationPolicy` defaults, the Lamar removal (the built-in `ServiceProvider` is now the container), and the AOT story.

---

## Net

**33 releases, two major new feature tracks shipped end-to-end, 17+ community bug reports triaged or fixed, and Aspire integration begun — in one week.**

If you're building event-sourced multi-tenant systems on .NET, the per-tenant partitioning feature alone removes a real scalability ceiling that traditionally pushed people toward either app-level sharding or completely separate databases per tenant. If you're an F# shop that has been quietly watching from the sidelines, the `codegen write --language fsharp` work means the Critter Stack is now a first-class option.

Find the work on GitHub (https://github.com/JasperFx), the docs at https://martendb.io and https://wolverinefx.io, or the JasperFx Discord. Issues and PRs from real adopters are landing within hours — if you're using this stack and hit something rough, file it.

#dotnet #csharp #fsharp #eventsourcing #postgresql #multitenancy #microservices #Marten #Wolverine #CritterStack
