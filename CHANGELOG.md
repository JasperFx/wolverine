# Changelog

## Unreleased

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
