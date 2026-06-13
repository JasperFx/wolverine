# A Big Week for the Critter Stack: Marten 9.3, Wolverine 6.2, Polecat 4.2

The Critter Stack 2026 majors went stable just over a week ago, and the post-GA cadence has been intense. Between **May 22 and May 29**, we shipped **8 Marten releases, 4 Polecat releases, and 4 Wolverine releases** — a mix of feature drops, performance work, source-generator polish, and a steady stream of real-adopter bug fixes. Here's a tour of what's new.

---

## Release Timeline

| Day | Marten | Polecat | Wolverine |
|-----|--------|---------|-----------|
| May 23 | — | 4.1.0 | — |
| May 24 | 9.0.1 | 4.1.1 | — |
| May 25 | 9.0.2 | — | — |
| May 27 | 9.2.0 | 4.2.0 | — |
| May 28 | 9.2.1, 9.3.0 | 4.2.1 | 6.1.0, 6.2.0 |
| May 29 | 9.3.1, 9.3.2, 9.3.3 | — | 6.2.1, 6.2.2 |

The patch storm at the end is mostly the natural shape of GA adoption — real users surfaced subtle bugs, fixes landed within hours, and the major-version branches have settled rapidly.

---

## Marten 9.3.0 — Binary Events, PostGIS, and pgvector

This is the biggest feature release of the week. Three sizable additions all land in one ship:

### 🆕 Binary Event Serialization

Opt individual event types into a binary wire format on a per-event-type basis — MemoryPack out of the box, or bring your own `IEventBinarySerializer`. JSON-serialized and binary-serialized events **coexist in the same `mt_events` table**, so the feature can be turned on in an existing system **with no data migration**.

```csharp
opts.Events.UseBinarySerialization(b =>
{
    b.MapBinaryEvent<OrderPlaced>(); // opt in by event type
});
```

Works on every `EventAppendMode` (Rich, Quick, QuickWithServerTimestamps) and through `BulkEventAppender`. There's a new optional NuGet, **`Marten.MemoryPack`**, for the MemoryPack-backed implementation.

📖 [Binary Event Serialization docs](https://martendb.io/events/binary-serialization) · 📖 [Versioned-event-types schema-evolution recommendation](https://martendb.io/events/binary-serialization#schema-evolution-use-versioned-event-types)

### 🆕 `Marten.PostGIS` — Spatial Queries

A new opt-in package wires up PostGIS, NetTopologySuite, and GeoJSON serialization, then exposes four geometry-aware query helpers:

```csharp
options.UsePostGIS();

// On any IQuerySession:
var nearest      = await session.NearestToAsync<Cafe>(location, take: 10);
var withinRadius = await session.WithinDistanceAsync<Cafe>(location, meters: 500);
var containing   = await session.ContainingAsync<Cafe>(point);
var crossing     = await session.IntersectingAsync<Cafe>(polygon);
```

`UsePostGIS()` enables the `postgis` extension on every database Marten manages — multi-tenant aware.

📖 [PostGIS docs](https://martendb.io/postgres/postgis)

### 🆕 `Marten.PgVector` — Vector Similarity Search

Companion package for embedding-based search:

```csharp
options.UsePgVector();

// Persist embeddings…
public class VectorProjection<TDoc> { /* base class with embedding hooks */ }

// …then search:
var results = await session.VectorSearchAsync<Document>(queryEmbedding, topK: 25);
```

Also addresses [#2515](https://github.com/JasperFx/marten/issues/2515) — extensions are now installed inside tenant databases under conjoined and per-database tenancy.

📖 [pgvector docs](https://martendb.io/postgres/pgvector)

### Fix worth flagging

The closed-shape storage rewrite in Marten 9 missed reading back `mt_created_at`, so `[CreatedAt]`-annotated members and `m.CreatedAt.MapTo(...)`-mapped fields weren't being populated after a load. [#4577](https://github.com/JasperFx/marten/pull/4577) restores the v8 behavior. If you saw "the timestamp is suddenly always default" after upgrading to 9.x, this is the patch.

---

## Marten 9.2.0 / 9.2.1 — Lightweight Sessions Become the Default

Two coordinated releases finally close out a long-standing semantic shift:

```csharp
// In Marten 9.2.1, the IDocumentSession resolved from DI is now a
// LightweightSession by default — no identity map, no dirty tracking.
//
// If you previously depended on the identity-mapped default, opt in:
opts.UseIdentityMapSessions(); // or call OpenSession() explicitly
```

This was the change captured in [#4574](https://github.com/JasperFx/marten/pull/4574) — "ACTUALLY making `LightweightSessions` the default." It's a quiet but high-impact semantic change. The migration guide has full detail; **if you upgraded from Marten 8 and your code depended on identity-map semantics from the default session, read [the lightweight-sessions docs](https://martendb.io/documents/sessions.html#session-types) before deploying.**

Other 9.2.0 highlights:

- **`IEventStore.AllDatabases()`** — store-agnostic accessor for every backing `IEventDatabase`, letting tooling (like CritterWatch) reach dead-letter counts, projection progress, and so on without coupling to a specific store type.
- JasperFx 2.2.0 family bump across the board.

---

## Marten 9.0.1 / 9.0.2 — The Source-Generator Patch Wave

The first 72 hours after GA were dominated by source-generator polish. Several adopters hit edge cases the alpha cycle didn't surface:

- **[#4557](https://github.com/JasperFx/marten/issues/4557)** — self-aggregating projections failed for consumers that referenced only the `Marten` package. The generator now ships **bundled as an analyzer in the `Marten` NuGet itself**, so a plain `<PackageReference Include="Marten" />` runs it automatically.

- **[#4542](https://github.com/JasperFx/marten/issues/4542)** — `required` members on self-aggregating snapshot types broke generated evolver construction. Fixed; `default!` is now only emitted when a public parameterless constructor exists, otherwise `RuntimeHelpers.GetUninitializedObject` takes over.

- **[#4543](https://github.com/JasperFx/marten/issues/4543)** — nullable `[ReadAggregate]` aggregate parameters generate correctly.

- **Self-aggregating `record` aggregates** no longer need a `Snapshot<T>` call site or `partial` — the generator now emits a self-aggregating evolver from the `record` declaration itself, including cross-assembly cases.

- **Opt-in STJ source-generation context** for AOT/trim-friendly metadata: `SystemTextJsonSerializer.UseTypeInfoResolver(...)` ([#4540](https://github.com/JasperFx/marten/issues/4540)).

Big thanks to community contributors — especially **@erdtsieck** — for stress-testing the new source generator in real apps and filing the precise reports that made these fixes possible.

---

## Wolverine 6.1.0 — AOT-Friendly Discovery (No More Runtime Type Scanning)

Wolverine 6.1.0 closes out the AOT pillar in a big way: **under `TypeLoadMode.Static`, Wolverine no longer does runtime `GetTypes()` / filesystem assembly scans for handlers, HTTP endpoints, gRPC services, or extensions.** Discovery now flows through **source-generated manifests** baked at build time.

The full list:

| What's source-generated now | PRs |
|-----------------------------|-----|
| Handlers + message types | [#2906](https://github.com/JasperFx/wolverine/issues/2906) · [#2928](https://github.com/JasperFx/wolverine/pull/2928) |
| HTTP endpoints | [#2925](https://github.com/JasperFx/wolverine/issues/2925) · [#2929](https://github.com/JasperFx/wolverine/pull/2929) |
| gRPC services (incl. direct-mapped mode) | [#2926](https://github.com/JasperFx/wolverine/issues/2926) · [#2930](https://github.com/JasperFx/wolverine/pull/2930) · [#2934](https://github.com/JasperFx/wolverine/pull/2934) |
| `[WolverineHandlerModule]` assemblies (no filesystem probe) | [#2905](https://github.com/JasperFx/wolverine/issues/2905) · [#2935](https://github.com/JasperFx/wolverine/pull/2935) |
| Extension discovery manifest | [#2902](https://github.com/JasperFx/wolverine/issues/2902) · [#2918](https://github.com/JasperFx/wolverine/pull/2918) |
| Generated HTTP/gRPC types attached by full name | [#2908](https://github.com/JasperFx/wolverine/issues/2908) · [#2936](https://github.com/JasperFx/wolverine/pull/2936) |
| Everything else routed through JasperFx `TypeQuery` | [#2909](https://github.com/JasperFx/wolverine/issues/2909) · [#2932](https://github.com/JasperFx/wolverine/pull/2932) |

📖 [AOT Publishing guide](https://wolverinefx.io/guide/aot.html)

The 6.1.0 release also includes a handful of new features that shipped through the 6.0.x patch line but didn't have a standalone GitHub release:

### `dotnet run -- wolverine-diagnostics describe-handlers <Type>`

CLI diagnostic that runs `DescribeHandlerMatch` from the command line — debug handler discovery without editing your bootstrapping code.

📖 [Command Line Diagnostics tutorial](https://wolverinefx.io/tutorials/command-line-diagnostics.html) · 📖 [Troubleshooting handler discovery](https://wolverinefx.io/guide/handlers/discovery.html#troubleshooting-handler-discovery)

### `dotnet run -- openapi`

Build-time OpenAPI generation. No database or broker needed — generates the doc straight from the source-generated endpoint manifest.

```bash
dotnet run -- openapi --output ./swagger.json
```

📖 [Command Line Integration](https://wolverinefx.io/guide/command-line.html)

### Production-grade Roslyn-free deployment

Docs + a worked sample showing how to drop `WolverineFx.RuntimeCompilation` from production images entirely once you're shipping pre-generated code.

📖 [Code Generation guide](https://wolverinefx.io/guide/codegen.html)

### Other quality-of-life landings in 6.1.0
- **`MessageGroupId` on standard SQS queues** for SQS fair queues ([#2889](https://github.com/JasperFx/wolverine/pull/2889))
- **`ClaimsPrincipal` on `SignalREnvelope`** for message-level auth on SignalR transports ([#2937](https://github.com/JasperFx/wolverine/pull/2937))
- **EF Core outbox now flushes before the HTTP response is written** ([#2920](https://github.com/JasperFx/wolverine/pull/2920))
- **Mixed-lifetime `IEnumerable<T>` support** + Lamar fully removed; the built-in `ServiceProvider` is the container ([#2914](https://github.com/JasperFx/wolverine/pull/2914))

---

## Wolverine 6.2.0 — `Result<T>`, DbContext Abstractions, and a 90% Allocation Cut

### 🆕 Native `Result<T>` Support — Phase 1

First three phases of `Result<T>`-style handler return support landed: a `ResultPolicy` registry, handler-side unwrapping seams, and caller-side `InvokeAsync<T>` unwrap. This lays the groundwork for plugging in `ErrorOr`, `OneOf`, `FluentResults`, and similar libraries as first-class return-value shapes ([#2952](https://github.com/JasperFx/wolverine/pull/2952), refs the long-running [#2221](https://github.com/JasperFx/wolverine/issues/2221)).

### 🆕 EF Core DbContext Abstractions

The EF Core transaction middleware now binds correctly when handler parameters are declared as **interface or abstract base types over a concrete `DbContext`**:

```csharp
public interface IOrdersDbContext
{
    DbSet<Order> Orders { get; }
    Task<int> SaveChangesAsync(CancellationToken ct);
}

public static class PlaceOrderHandler
{
    public static async Task Handle(PlaceOrder cmd, IOrdersDbContext db)
    {
        db.Orders.Add(new Order(cmd.OrderId, cmd.Total));
        // Wolverine auto-applies the EF Core transaction & flushes the outbox.
    }
}
```

Multiple abstractions over the same concrete `DbContext` are supported in a single handler — the runtime resolves them all to the same scoped instance, and the transaction still auto-applies.

📖 [DbContext Abstractions docs](https://wolverinefx.io/guide/durability/efcore/transactional-middleware.html#dbcontext-abstractions)

### ⚡ Outgoing Envelope Pooling

`MessageRouter.RouteForPublish` now pulls from the runtime envelope pool when the route's sender is an `InlineSendingAgent` or `BufferedSendingAgent`. Measured in the [CritterStackScalability harness](https://github.com/JasperFx/CritterStackScalability/blob/main/src/Scalability.WolverineRuntime/WolverineTransportBenchmarks.cs):

- **−504 B/op (−90%)** on transport-bound publish/send paths
- **~10× fewer Gen0 collections** per 1k ops

`DurableSendingAgent`, local-queue agents, and `ISenderRequiresCallback` senders are excluded for now (different lifecycle plumbing required, tracked as follow-ups).

### Bug fixes worth noting

- **Scheduled-cascade loss from `[ReadAggregate]` / `[DocumentExists]` handlers** ([#2943](https://github.com/JasperFx/wolverine/pull/2943))
- **Long Postgres queue names** now routed through Weasel's `PostgresqlIdentifier.Shorten()` so they don't overflow the 63-byte identifier limit ([#2945](https://github.com/JasperFx/wolverine/pull/2945))
- **MySQL `PersistNodeRecord` SQL syntax error** — unquoted schema identifier emit ([#2946](https://github.com/JasperFx/wolverine/pull/2946))
- **Pulsar `KeyNotFoundException` ACKing batch messages on partitioned topics** ([#2950](https://github.com/JasperFx/wolverine/pull/2950))
- **Remote-node agent `InvokeAsync<T>` reply timeout** bumped 10s → 30s ([#2951](https://github.com/JasperFx/wolverine/pull/2951))

### Docs

- **AOT callout** for FluentValidation in the [HTTP validation guide](https://wolverinefx.io/guide/http/validation.html)
- Extended docs + scenario tests for the new DbContext abstractions

---

## Polecat 4.1.0 / 4.2.0 — Dead-Letter Storage, LINQ DistinctBy, and Resilience Hardening

Polecat (SQL Server-backed sibling of Marten) had a similarly productive week.

### Polecat 4.1.0 highlights

**Projection dead-letter storage**: failures under `SkipApplyErrors` (the JasperFx.Events 2.x default) are now persisted as `DeadLetterEvent` documents in `pc_doc_deadletterevent`, mirroring Marten's document-backed dead letters. The `IEventDatabase` count reads are implemented:

```csharp
await database.CountDeadLetterEventsAsync(shardName);
var counts = await database.FetchDeadLetterCountsAsync();
```

**Resilience-pipeline hardening**: all database command execution now flows through `StoreOptions.ResiliencePipeline` (Polly). Previously-unprotected paths are now covered — event-store progression reads, `IEventStore` explorer reads, the synchronous HiLo sequence path, and the LINQ non-stale-data poll.

### Polecat 4.2.0 highlights

**LINQ `DistinctBy()` translated to SQL Server**:

```csharp
var distinct = await session.Query<Order>()
    .DistinctBy(o => o.CustomerId)
    .ToListAsync();
```

Emitted as a `ROW_NUMBER()` windowed subquery partitioned by the key (SQL Server has no `DISTINCT ON`).

**`IEventStore.AllDatabases()` override** — same store-agnostic surface Marten 9.2.0 exposed, implemented on Polecat's `DocumentStore`.

---

## Cross-Stack Theme: Coordinated Version Matrix

A meta-theme of the week is how tightly the version matrix is now coordinated. A typical patch wave looks like:

```
JasperFx 2.2.1 ─┬─► Marten 9.3.x
                ├─► Polecat 4.2.x
                └─► Wolverine 6.2.x
```

A single change in `JasperFx.Events.SourceGenerator` (say) gets repoint PRs through every dependent repo within the same window — usually within hours. If you're on the GA majors, you'll see frequent patch upgrades, but **NuGet's `[*, *)` solver and the matched alpha pinning in `Directory.Packages.props` mean the matrix stays consistent**.

---

## Community Spotlight

This week's release notes are full of fixes traceable to specific community reports:

- **@erdtsieck** — multiple source-generator edge cases, the `messageContext` duplicate-var codegen fix, the `IReadOnlyList` aggregate grouper signature
- **@LiteracyFanatic** — SQS standard-queue `MessageGroupId` support
- **@patrick-cloke-simplisafe** — Pulsar partitioned-topic ACK fix
- **@XL1TTE** — EF Core DbContext abstractions PR + MediatR shim handler discovery
- **@kentcooper** — duplicate `messageContext` bug report
- **@isccliao** — MySQL ANSI quoting bug
- **@dbrcina** — default-session-now-lightweight semantic regression
- **@trung-nguyenduc** — LINQ `DistinctBy()` translation gap (fixed for SQL Server in Polecat)
- **@gergelyurbancsik** — `LoadAsync<T>` under `ConjoinedDefault` tenancy
- **@MeikelLP** — source-generator delivery bug
- **@dmytro-pryvedeniuk** — broader resource disposal cleanup

If you're an adopter who hits something rough on the new GAs, file it. Most of the items above were reported and fixed in the same week.

---

## Upgrading

For most adopters, the safe upgrade path right now is:

```
Marten 9.3.3 + Polecat 4.2.1 + Wolverine 6.2.2 + JasperFx.* 2.2.1
```

If you're upgrading from Marten 8, the two semantic shifts to know about are:

1. **Default `IDocumentSession` is now lightweight.** See [the migration guide](https://martendb.io/migration-from-8-to-9.html).
2. **Marten 9 uses source-generated projection dispatch** — your self-aggregating types should compile out of the box now (post-9.0.2), but `partial` is still required on `*Projection` subclasses.

If you're upgrading from Wolverine 5, the [Wolverine 5 → 6 migration guide](https://wolverinefx.io/guide/migration.html) covers `ServiceLocationPolicy`, the Lamar removal, and the AOT story.

---

## What's Next

Looking ahead, the open work this week paints a clear picture:

- **`Result<T>` Phases 2+** — more idiomatic unwrapping, integration with the popular `Result` libraries
- **CritterWatch clustering + RBAC + MCP** — a new product surface taking shape on top of the stable libraries
- **Cold-start performance pillar continuation** — the source-generator wave is opening up further startup-snapshot work

The Critter Stack is in a healthy place: stable majors, sub-day patch cadence on real bugs, and a noticeably busy community PR queue. Keep the issues, PRs, and discussions coming.

Find us on the [JasperFx Discord](https://discord.gg/WMxrvegf8H) or open issues across the [JasperFx GitHub org](https://github.com/JasperFx).
