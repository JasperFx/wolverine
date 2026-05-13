# Changelog

## Unreleased

## 6.0.0-alpha.1

First explicitly-versioned 6.0 alpha. Cumulative work since 5.39.0 on the `main`
branch. **See the [migration guide](https://wolverinefx.net/guide/migration.html#key-changes-in-6-0)
for the full breaking-change inventory and the at-a-glance table.**

### WolverineFx (core)

- **Dropped `net8.0` support.** Target frameworks are now `net9.0;net10.0`. The
  JasperFx 2.0-alpha line that 6.0 builds on no longer targets net8.0. (BREAKING)
- **Bumped the critter-stack dependency line** to `JasperFx 2.0.0-alpha.*`,
  `JasperFx.Events 2.0.0-alpha.*`, `Marten 9.0.0-alpha.*`, `Polecat 4.0.0-alpha.*`.
  (BREAKING)
- **`WolverineOptions.ServiceLocationPolicy` default flipped** from
  `AllowedButWarn` to `NotAllowed` (#2584). Apps that previously relied on
  Wolverine's code generation falling back to service location at runtime now
  throw `InvalidServiceLocationException` on startup. Restructure registrations
  or allow-list per type via `opts.CodeGeneration.AlwaysUseServiceLocationFor<T>()`.
  Soft-landing: `opts.RestoreV5Defaults()` flips this back. (BREAKING)
- **Pooled `Envelope` instances at the two `Executor.InvokeAsync` sites** for the
  internal receive pipeline (#2741 closing part of #2726). Allocation reduction
  on the hot path; no public API change. Gated on `ActiveSession == null` so
  tracking sessions, observer tests, and the `ITrackedSession.Events` capture-
  after-handler scenario all keep fresh allocations.
- **New `WolverineOptions.RestoreV5Defaults()`** one-line migration affordance.
  Restores every changed runtime default back to its 5.x value (today that means
  `ServiceLocationPolicy`; more lines get appended as additional defaults flip
  in 6.x patch releases).
- **Stale `DefaultSerializer` XmlDoc fixed.** The doc-comment had claimed
  Newtonsoft.Json was the default; STJ has actually been the default since
  Wolverine 5.0.
- **Performance: per-endpoint serializer cache pre-population** during
  `Endpoint.Compile()`. Hot-path serializer lookup is now a pure read.
- **`Subscription.Scope` JSON converter** swapped from Newtonsoft's
  `[StringEnumConverter]` to STJ's `[JsonStringEnumConverter]`. Wire format
  unchanged (still string-named scopes).

### WolverineFx.Newtonsoft (new package)

- **Extracted all Newtonsoft.Json integration** out of core `WolverineFx` into a
  new separate `WolverineFx.Newtonsoft` package (#2743). Core `WolverineFx`
  no longer depends on `Newtonsoft.Json`. The 5.x APIs (`UseNewtonsoftForSerialization`,
  `CustomNewtonsoftJsonSerialization`, `IMassTransitInterop.UseNewtonsoftForSerialization`,
  the `NewtonsoftSerializer` type) are now **extension methods** in the new
  package — same call shape, just need `dotnet add package WolverineFx.Newtonsoft`
  + `using Wolverine.Newtonsoft;`. (BREAKING)
- Transports that pin a `NewtonsoftSerializer` internally for NServiceBus /
  MassTransit wire-compat (RabbitMQ's `UseNServiceBusInterop()`, the AWS SQS
  and SNS NServiceBus mappers, Azure Service Bus listeners) carry the
  `WolverineFx.Newtonsoft` dependency on consumers' behalf.

### Namespace moves (BREAKING)

- `SnapshotLifecycle` moved from `Marten.Events.Projections` to
  `JasperFx.Events.Projections`.
- `OperationRole` moved from `Marten.Internal.Operations` to `Weasel.Core`.

### Foundation

- **AOT pillar foundation landed** (#2747 toward #2715 / #2746). New
  `Wolverine.AotSmoke` regression-guard project + `.github/workflows/aot.yml`
  workflow. Verifies the AOT-clean *subset* of Wolverine's surface (Envelope
  value-shape, DeliveryOptions, WolverineOptions configuration, scheduling
  helpers). The full per-file annotation pass and the eventual flip of
  `IsAotCompatible=true` on `Wolverine.csproj` is tracked in #2746.

## 5.37.2

### WolverineFx (core)

- Removed the experimental Wolverine-specific Roslyn source generator (`Wolverine.SourceGeneration`)
  and the `IWolverineTypeLoader` / `[WolverineTypeManifest]` / `CompositeWolverineTypeLoader`
  surface it produced. The compile-time handler-discovery path was never wired up to anything in
  steady state — handler graph compilation always falls back to `compileWithRuntimeScanning`, which
  has been the only code path exercised by tests and downstream consumers. Stripping it removes a
  netstandard2.0 analyzer DLL from the WolverineFx NuGet, the analyzer ProjectReference from
  `Wolverine.csproj`, the source-gen branches in `ExtensionLoader.ApplyExtensions`,
  `WolverineRuntime.HostService` startup, `HandlerGraph.Compile`, and `HandlerChain.AttachTypes`,
  plus the two `TypeLoaderManifestModule*` test fixtures and their aggregation tests. The
  `JasperFx.SourceGeneration` analyzer (separate package) is unaffected.

## 5.37.0

### WolverineFx.Marten

- Fixed durable local messages from a main Marten store being routed to the wrong inbox when handled by an
  ancillary-store handler (`[MartenStore(typeof(...))]`). The publisher-stamped `envelope.Store` (the main store)
  carried through the inbox and `FlushOutgoingMessagesOnCommit` then pointed at the publisher's
  `wolverine_incoming_envelopes` table while the receiving Marten session was connected to the ancillary
  database, surfacing as `42P01: relation "public.wolverine_incoming_envelopes" does not exist`. Closes #2669.
  - The receiving handler's ancillary-store association now wins over the publisher's: `assignAncillaryStoreIfNeeded`
    in `DurableLocalQueue` and `DurableReceiver` no longer short-circuits when the envelope already has a `Store`.
  - The Marten listener's in-transaction inbox `UPDATE` is gated on `Uri` equality (not `IMessageStore.Id`) so
    cross-store envelopes deterministically skip the in-transaction shortcut and the envelope's owning store
    handles the mark-handled separately.

### WolverineFx.RabbitMQ

- Added a public fluent API for multi-node RabbitMQ cluster failover via `RabbitMqTransportExpression.AddClusterNode(...)`.
  Two repeatable overloads — `AddClusterNode(string hostName, int port = -1)` (copies the factory's `Ssl` settings
  onto the new endpoint) and `AddClusterNode(AmqpTcpEndpoint endpoint)` (power-user). Cluster nodes propagate to
  virtual-host tenants and surface in connection diagnostics. Closes #2659.

### WolverineFx.Polecat

- Fixed `FlushOutgoingMessagesOnCommit` `NullReferenceException` on every Polecat-backed handler. The
  `OutboxedSessionFactory` was constructing the listener with `null!` for the `SqlServerMessageStore` on the
  assumption that a post-construction setter would fill it in — but the listener's field is `readonly`. Replaced
  with a `resolveSqlServerMessageStore()` helper that mirrors `PolecatEnvelopeTransaction`'s two-shape
  resolution (`SqlServerMessageStore` + `MultiTenantedMessageStore { Main: SqlServerMessageStore }`) so multi-tenanted
  Polecat works too. Closes #2668.

### WolverineFx (core)

- New `DocumentStores` collection on `ServiceCapabilities`, mirroring the existing `EventStores` walk for the
  document side. Walks `IDocumentStoreUsageSource` registrations (Marten and Polecat both implement it via
  `IDocumentStore`), dedupes by `Subject` URI to avoid double-counting when the same instance wears both
  event-store and document-store hats, and stuffs `DocumentStoreUsage` snapshots into the capabilities surface
  so CritterWatch can render document-side configuration the same way it already renders event stores.

### Dependencies

- Bumped `JasperFx` 1.28.2 → 1.29.0, `JasperFx.Events` 1.31.1 → 1.33.1, `Marten` + `Marten.AspNetCore`
  8.32.0 → 8.35.0. The bumped JasperFx packages provide the `IDocumentStoreUsageSource` and `DocumentStoreUsage`
  types the new capability surface depends on.

## 5.36.2

### WolverineFx (core)

- Reworked the EF Core + outbox flush pipeline to ensure cascading messages aren't sent before the EF Core
  transaction commits. New `IFlushesMessages` abstraction; `EnrollDbContextInTransaction` and the HTTP chain
  codegen now route the post-handler flush through it so the commit-then-flush ordering is enforced
  consistently. Companion fix to 5.36.1 — the codegen guard from 5.36.1 stopped emitting the duplicate flush
  call, this release reworks the underlying machinery so the ordering invariant holds even under future
  codegen changes.

## 5.36.1

### WolverineFx.EntityFrameworkCore

- Fixed a code-generation bug where the EF Core transactional middleware in Eager mode (the default) emitted
  a duplicate `messageContext.FlushOutgoingMessagesAsync()` call BEFORE the wrapping
  `efCoreEnvelopeTransaction.CommitAsync(...)`. The early flush sent cascading messages through the transport
  sender while the EF Core transaction (and its `wolverine_outgoing_envelopes` row) was still uncommitted, so
  the post-send `IMessageOutbox.DeleteOutgoingAsync` ran on a separate connection that couldn't see the
  uncommitted INSERT — the row was left stranded for the durability agent to re-send (at-least-once instead of
  exactly-once). Only manifested on HTTP endpoints; message handler chains were unaffected. Lightweight mode
  is unchanged. Reported via the sample at https://github.com/dmytro-pryvedeniuk/outbox.

## 5.36.0

### WolverineFx.Http

- Added native API versioning support via `Asp.Versioning.Abstractions` 10.x. Supports URL-segment versioning
  (`/v1/...`, `/v2/...`), sunset/deprecation policies with RFC 9745/8594/8288 response headers, and automatic
  OpenAPI document partitioning with Swashbuckle/Scalar/Microsoft.AspNetCore.OpenApi. No dependency on
  `Asp.Versioning.Http` — versioning is driven entirely via `IHttpPolicy`.
  See [versioning guide](docs/guide/http/versioning.md).
