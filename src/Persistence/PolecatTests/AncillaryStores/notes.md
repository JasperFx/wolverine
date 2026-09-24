# Polecat ancillary-store tests (GH-3109)

Polecat mirror of `MartenTests/AncillaryStores`. Polecat is SQL-Server-backed, so these use
`Servers.SqlServerConnectionString` and create real SQL Server databases for the separate-database
scenarios.

## Coverage map vs the Marten battery

| Marten test | Polecat mirror |
|---|---|
| `bootstrapping_ancillary_marten_stores_with_wolverine` | `bootstrapping_ancillary_polecat_stores_with_wolverine` (registration + `OutboxedSessionFactory<T>` + end-to-end `[PolecatStore]` middleware) |
| `ancillary_stores_use_different_databases` | `ancillary_stores_use_different_databases` (separate SQL Server databases + multi-tenanted store → `MultiTenantedMessageStore` + durability agents) |
| `ancillary_store_subject_uri_uniqueness` | `ancillary_store_subject_uri_uniqueness` (asserts the per-store agent `Uri`, see note below) |
| `multi_stream_projection_with_side_effects_on_ancillary_store` | `inline_projection_side_effects_on_ancillary_polecat_store` (inline projection `RaiseSideEffects` → `PublishMessage` relayed through the Wolverine outbox) |
| `tenant_partitioned_ancillary_store` | *(not mirrored — see below)* |
| *(generic attribute — new in GH-3109)* | `storage_attribute_routes_to_polecat_store` (`[Storage(typeof(T))]` parity with `[PolecatStore]`) |
| `deduplication_rides_an_ancillary_marten_transaction` | `deduplication_rides_an_ancillary_polecat_transaction` (GH-4605; the claim table is resolved through the store marker, so it must land in the ancillary schema and roll back with it) |

## Polecat-specific differences

* **No `UseTenantPartitionedEvents`.** Polecat multi-tenancy is *separate database per tenant*, not
  Marten's Conjoined + partitioned-events model. So Marten's `tenant_partitioned_ancillary_store`
  (which relies on `Events.UseTenantPartitionedEvents` + `AddMartenManagedTenantsAsync`) has no
  verbatim Polecat equivalent. The multi-tenant ancillary coverage instead lives in
  `ancillary_stores_use_different_databases` (database-per-tenant → `MultiTenantedMessageStore`).

* **`SubjectUri` vs `Uri`.** The RDBMS message store only specializes `SubjectUri` for the `Main`
  role, so SQL-Server ancillary stores share `wolverine://messages`. The per-store identity that
  must be unique on SQL Server is the agent `Uri` (`engine/server/database/envelope-schema`), which
  is what `ancillary_store_subject_uri_uniqueness` asserts.

* **The ancillary envelope schema does not default to the store's document schema.** Marten's
  ancillary `IntegrateWithWolverine` falls through to `store.Options.DatabaseSchemaName`; Polecat's
  (`AncillaryWolverineOptionsPolecatExtensions`) falls through to the literal `"wolverine"`, the same
  as the primary store's own fallback would on a host that set `Durability.MessageStorageSchemaName`.
  For ancillary stores in *separate databases* that is harmless. For ancillary stores that are
  *schemas in one database* — the shape `storage_attribute_routes_to_polecat_store` and
  `deduplication_rides_an_ancillary_polecat_transaction` use — it means both stores' Wolverine tables
  collide unless `IntegrateWithWolverine(x => x.SchemaName = ...)` is spelled out. Worth knowing before
  writing a test here that asserts "which schema did the row land in".

* **Inline (not async-daemon) side effects.** Polecat relays projection side effects through
  `StoreOptions.Events.MessageOutbox`. The Wolverine integration wires the ancillary store's outbox
  to `PolecatToWolverineOutbox` (via `PolecatOverrides<T>`), and the per-store opt-in is
  `Events.EnableSideEffectsOnInlineProjections`.
