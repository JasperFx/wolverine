# Epic Plan: Conjoined Multi-Tenancy for EF Core in Wolverine

**Date:** 2026-07-18
**Status:** FILED — all GitHub issues created 2026-07-18, pending Jeremy review

**Issue map:**
- Master tracking: [wolverine#3465](https://github.com/JasperFx/wolverine/issues/3465)
- Workstream A: [jasperfx#531](https://github.com/JasperFx/jasperfx/issues/531)
- Workstream B: [weasel#362](https://github.com/JasperFx/weasel/issues/362)
- Workstream C: Phase 1 [wolverine#3462](https://github.com/JasperFx/wolverine/issues/3462), Phase 2 [wolverine#3463](https://github.com/JasperFx/wolverine/issues/3463), Phase 3 [wolverine#3464](https://github.com/JasperFx/wolverine/issues/3464) (Phase 4 tracked on #3465)
- CritterWatch: [CritterWatch#720](https://github.com/JasperFx/CritterWatch/issues/720)
- Workstream D: [polecat#335](https://github.com/JasperFx/polecat/issues/335)
**Motivating context:** https://barretblake.dev/posts/development/2026/07/multi-tenant-part-1/ —
hand-rolled shared-database tenancy in EF Core 10 (tenant column + named query filters + raw-SQL
partition DDL inside EF migrations). Everything the author does manually, the critter stack
already automates for Marten. This epic brings that automation to EF Core users through
Wolverine, with Weasel owning partition DDL and CritterWatch owning tenant operations.

## Locked decisions (Jeremy, 2026-07-18)

1. **`ITenanted` lives in JasperFx.** Promote `ITenanted : IHasTenantId` into
   `JasperFx.MultiTenancy`; Marten and Polecat re-point their identical local markers at it
   (same dedupe motion as jasperfx#224 did for `IHasTenantId`). Wolverine EF Core keys off the
   JasperFx interface, so one marker works across Marten documents, Polecat documents, and EF
   entities.
2. **SQL Server gets physical partitioning in v1** of the Wolverine feature, via
   `Weasel.SqlServer.ManagedTenantPartitions`. A follow-up epic brings **Polecat** to full
   parity with Marten's per-tenant partitioning (today Polecat only partitions the events
   table; document tables are column+filter only and explicitly block partitioning).
3. **Tenant lifecycle registry is Wolverine-owned.** A small `wolverine_tenants` table (not an
   extension of Weasel's partition control tables) carries enable/disable state and is the
   authoritative tenant list for conjoined mode. Weasel's partition registries
   (`*_tenant_partitions` on PG, tenant-ordinal registry on SQL Server) stay implementation
   details of the partitioning layer.
4. **Conjoined sagas are in scope** for this epic (tenant-scoped load/insert/update/delete
   through the EF saga frames).

## Goals

- `ITenanted` on an EF entity ⇒ Wolverine makes it conjoined-multi-tenant with **zero**
  hand-written filters: `tenant_id` column mapped, global query filter bound to the ambient
  Wolverine tenant, tenant stamped on insert, cross-tenant writes rejected.
- Opt-in **Weasel-managed physical partitioning** of tenanted tables on both PostgreSQL
  (list partitions, value→suffix bucketing) and SQL Server (tenant-ordinal RANGE RIGHT).
- **Behavioral compliance with Marten/Polecat** conjoined semantics: `*DEFAULT*` sentinel,
  `TenantIdStyle` correction, stamp-on-write/hydrate-on-load, tenant-scoped deletes,
  additive-only partition migration.
- **CritterWatch tenancy management works out of the box**: add/disable/enable/remove/list
  tenants from the Tenants tab against a conjoined EF Core app, via a Wolverine-provided
  `IDynamicTenantSource<string>`.
- Conjoined **sagas**.

## Non-goals

- No change to the existing DB-per-tenant EF Core mode
  (`AddDbContextWithWolverineManagedMultiTenancy`); conjoined is a sibling mode, and the two
  are mutually exclusive per DbContext.
- No schema-per-tenant mode (the blog's Part 2 topic) — possible future work, not this epic.
- No EF-migrations authoring of partition DDL. Partitioned conjoined contexts require the
  Wolverine/Weasel-managed migration path; plain conjoined (no partitioning) works with either
  EF migrations or Weasel-managed migrations.
- MySQL/Oracle/SQLite partitioning (Weasel has none; conjoined column+filter mode still works
  anywhere EF Core does).

---

## Workstream A — JasperFx: promote `ITenanted`

Small, but it leads the release train (critter-stack versions move in lockstep).

- Add `public interface ITenanted : IHasTenantId {}` to `JasperFx.MultiTenancy` with the
  settable-`TenantId` doc contract both libraries already imply.
- Marten: retype `Marten.Metadata.ITenanted` as the JasperFx interface via
  `[TypeForwardedTo]`/alias (pattern already used for `TenancyStyle`). `TenancyPolicy`
  unchanged.
- Polecat: same for `Polecat.Metadata.ITenanted`.
- Sanity check: nothing in either library depends on the marker being locally declared
  (both are already empty extensions of `IHasTenantId`).

**Releases required:** JasperFx minor → Marten + Polecat patch/minor pin bumps.

### Draft issue (jasperfx repo) — *placeholder wording, review before filing*

> **Title:** Promote `ITenanted` marker into JasperFx.MultiTenancy
> **Body:** Marten (`Marten.Metadata.ITenanted`) and Polecat (`Polecat.Metadata.ITenanted`)
> declare identical empty markers extending `IHasTenantId`. Wolverine is about to need the
> same marker for conjoined EF Core tenancy. Move the interface to `JasperFx.MultiTenancy`
> so one marker drives conjoined behavior across all three, and have Marten/Polecat forward
> to it. Follows the `IHasTenantId` dedupe in jasperfx#224.

---

## Workstream B — Weasel.SqlServer: close the managed-partitioning gaps

`ManagedTenantPartitions` already exists (built for Polecat events, weasel#301) and is the
right mechanism. Before Wolverine and Polecat lean on it for *many* tables per app, audit and
close these gaps against the PG `ManagedListPartitions` feature set:

| # | Gap / audit item | PG behavior today | SQL Server today |
|---|---|---|---|
| B1 | **Drop semantics** | `DETACH PARTITION [CONCURRENTLY]` + `DROP TABLE` — tenant data is physically removed | `ALTER PARTITION FUNCTION … MERGE RANGE` — boundary disappears but **rows survive** into the neighboring partition. Decide + document: managed drop should (optionally?) `DELETE` the tenant's rows before merging, or the Wolverine/Polecat layer does the delete. Needed for CritterWatch `RemoveTenant`/hard-delete parity |
| B2 | **Bucketing** (many tenants → one partition) | `partition_value → partition_suffix` mapping is many-to-one by design | ordinal allocation is strictly `max+1` per tenant — no sharing. Add optional tenant→ordinal assignment (explicit ordinal on add) so buckets are possible; this is also the mitigation for the 15k-partition ceiling the blog post calls out |
| B3 | **Disabled-tenant awareness** | none (Marten layers it elsewhere) | none — fine; lifecycle stays in the Wolverine/Polecat registry (decision 3), no Weasel change |
| B4 | **Delta/migration ergonomics** | managed tables use `IgnorePartitionsInMigration`; additive-only runtime path documented (marten#4706/#4713) | `TableDelta` deliberately skips managed strategies — verify a *new* table added to an existing managed set gets back-filled with all existing tenant ordinals on migration (the PG additive path has explicit handling; confirm/port) |
| B5 | **Multi-table batch add** | `AddPartitionToAllTables(logger, db, dict)` returns `TablePartitionStatus[]` | overloads exist; confirm status reporting parity so Wolverine can surface per-table results to CritterWatch |

Deliverable: short gap-closure PR(s) to Weasel + a documented contract that both Polecat and
Wolverine build on.

**Releases required:** Weasel minor (before Wolverine Phase 2 and the Polecat epic).

### Draft issue (weasel repo) — *placeholder wording*

> **Title:** `ManagedTenantPartitions` (SQL Server) parity audit vs `ManagedListPartitions`
> **Body:** Polecat currently uses `ManagedTenantPartitions` for the events table only.
> Wolverine's conjoined EF Core tenancy epic and the Polecat document-partitioning follow-up
> will apply it across many tables per application. Close the gaps: (1) data-removing drop
> semantics (MERGE RANGE leaves rows behind, unlike PG detach+drop); (2) optional explicit
> ordinal assignment to allow tenant bucketing; (3) confirm new-table back-fill of existing
> ordinals under migration; (4) batch add/status parity.

---

## Workstream C — Wolverine.EntityFrameworkCore: the epic proper

### Phase 1 — Conjoined core (no partitioning yet)

New registration mode in `WolverineEntityCoreExtensions`:

```csharp
// name TBD — see interview questions
services.AddDbContextWithWolverineManagedConjoinedTenancy<ItemsDbContext>(
    (builder, connectionString) => builder.UseNpgsql(connectionString));
```

Components:

1. **Model convention** — extend `WolverineModelCustomizer` (already swapped in as EF's
   `IModelCustomizer` by every Wolverine registration path): for each entity implementing
   `JasperFx.MultiTenancy.ITenanted`:
   - map `TenantId` → `tenant_id` (`StorageConstants.TenantIdColumn`), default
     `*DEFAULT*` (`StorageConstants.DefaultTenantId`);
   - attach a **global query filter** `e.TenantId == context.TenantId` bound to a
     `CurrentTenant` accessor on the context (see 3);
   - index note: add `tenant_id` to the entity's key/index shape only in Phase 2
     (partitioning) — plain conjoined keeps the user's PK and adds a `tenant_id` index.
2. **`SaveChangesInterceptor`** (`TenantStampingInterceptor`):
   - on **Added**: stamp `TenantId` from the ambient tenant (after `TenantIdStyle`
     correction); explicit non-empty `TenantId` different from ambient ⇒ throw (matches
     Marten's conjoined write semantics — sessions write their own tenant only);
   - on **Modified/Deleted**: if the entry's `TenantId` ≠ context tenant ⇒ throw
     `CrossTenantWriteException` (name TBD). This is the "no forgotten
     `IgnoreQueryFilters()`" guarantee on the write side;
   - rejects writes for **disabled** tenants (registry check, Phase 3).
3. **Tenant-pinned DbContext** — a conjoined `IDbContextBuilder<T>`
   (`ConjoinedDbContextBuilder<T>`): single database (connection string from the app's
   single message store), but every built context is pinned to `MessageContext.TenantId`
   before handing to user code. Registering `IDbContextBuilder<T>` means the existing
   `EFCorePersistenceFrameProvider` multi-tenant codegen branch
   (`CreateTenantedDbContext<>` + `StartDatabaseTransactionForDbContext`) works **unchanged**
   — the builder is where conjoined vs DB-per-tenant differ, not the frames.
   - Tenant flow already exists end-to-end: `Envelope.TenantId` → `MessageContext.TenantId`
     with `TenantIdStyle.MaybeCorrectTenantId`, HTTP `ITenantDetection`
     (`opts.TenantId.IsQueryStringValue(...)` etc.), gRPC metadata detection. No new
     detection work.
   - Mechanism for pinning: prefer an injected `IWolverineTenantContext` (scoped) the filter
     lambda closes over, so users' own DbContext ctors don't change; fall back to a
     `WolverineDbContext` base-class property only if the filter-through-service approach
     fights EF's filter caching. (EF caches the filter expression per model; the
     tenant value must come from per-instance state — standard pattern is a field/property
     on the context populated at construction, which the builder does.)
4. **Outbox/transaction paths**: `EfCoreEnvelopeTransaction`, `IDbContextOutboxFactory.
   CreateForTenantAsync`, and `DbContextOutbox.TenantId` already carry tenant ids — audit
   that the conjoined builder path threads `TenantId` into all three (the factory currently
   assumes tenant ⇒ different database; conjoined means tenant ⇒ same database, pinned
   context).
5. **Conjoined sagas** (decision 4):
   - A saga type implementing `ITenanted` gets tenant-scoped persistence through
     `EFCorePersistenceFrameProvider`:
     - `DetermineLoadFrame`/`LoadEntityFrame`: load by (saga id + tenant). If the global
       query filter is active on the pinned context this may come for free — **verify
       `FindAsync`/keyed loads respect global query filters**; if not, generate an explicit
       `Where(id && tenant)` load;
     - insert: stamped by the interceptor;
     - update/delete + `IncrementSagaVersionIfNecessary` + `WrapSagaConcurrencyException`:
       unchanged, but the cross-tenant guard applies;
   - Identity stays the user's saga id (no composite-key requirement in Phase 1); a
     uniqueness decision is needed for Phase 2 partitioned sagas (see interview Q3).
6. **Message storage is untouched**: conjoined = one database ⇒ the plain single
   `PostgresqlMessageStore`/`SqlServerMessageStore`. No `MultiTenantedMessageStore`.
   Envelopes already persist `TenantId` for context restoration.

**Tests:** new `EfCoreTests.ConjoinedTenancy` battery + HTTP integration tests mirroring
`multi_tenancy_detection_and_integration.cs`; compliance assertions ported from Marten's
conjoined tests (default-tenant sentinel, style correction, stamping, hydration,
tenant-scoped delete, cross-tenant rejection). Both PG (5433) and SQL Server (1434), conn
strings via `Servers`.

### Phase 2 — Weasel-managed physical partitioning (opt-in)

```csharp
services.AddDbContextWithWolverineManagedConjoinedTenancy<ItemsDbContext>(
    ..., o => o.UsePartitioning());   // shape TBD
```

- **Migration ownership**: partitioned conjoined contexts **require**
  `UseEntityFrameworkCoreWolverineManagedMigrations()` /
  `EntityFrameworkCoreSystemPart`. EF migrations cannot express PG declarative partitioning
  or SQL Server partition schemes — this is exactly the raw-SQL hack the blog resorts to.
  Guard with a clear bootstrap error if partitioning is on and EF migrations are.
- **Translation**: from the EF `IModel`, build Weasel `Table`s for each `ITenanted` entity
  (via `Weasel.EntityFrameworkCore`, already referenced):
  - PG: `PARTITION BY LIST (tenant_id)`,
    `.UsePartitionManager(managedListPartitions)`, `IgnorePartitionsInMigration = true`;
    a `ManagedListPartitions` feature with control table
    `wolverine_tenant_partitions` in the durability schema (Marten uses
    `mt_tenant_partitions`; ours is Wolverine-owned and separate);
  - SQL Server: `PartitionByManagedTenants(managedTenantPartitions)` with the ordinal
    registry table; the model convention adds an `int tenant_ordinal` **shadow property**
    to `ITenanted` entities, and `TenantStampingInterceptor` stamps it from
    `ManagedTenantPartitions.Ordinals[tenantId]`.
- **PK shape**: partition column must be in the PK/unique keys. The convention rewrites
  `ITenanted` entity keys to composite (`tenant_id`/`tenant_ordinal` + id), with an
  ordering option mirroring Marten's `PrimaryKeyTenancyOrdering` (default
  `TenantId_Then_Id`, Marten's V9 default).
- **Bucketing**: expose Marten-style tenant→suffix mapping on the add-tenant API
  (`AddTenantAsync(tenantId, partitionSuffix)`-shaped) so N tenants can share a partition —
  the answer to SQL Server's 15k-partition ceiling and to "small tenants don't deserve their
  own partition". SQL Server bucketing depends on Weasel gap B2.
- **Suffix/identifier hygiene**: reuse Weasel's `ListPartition.SanitizeSuffix` + port
  Marten's PG identifier-legality and 63-byte checks.

### Phase 3 — Tenant registry, `IDynamicTenantSource`, CritterWatch

- **`wolverine_tenants` registry table** (Wolverine-owned, durability schema; created as a
  Weasel `FeatureSchemaBase` + `IDatabaseInitializer` like the partition registries):
  `tenant_id varchar PK`, `is_disabled bit`, `partition_suffix varchar null`,
  `created_utc`/`modified_utc`. Authoritative tenant list for conjoined mode — exists in
  **both** plain-conjoined and partitioned-conjoined (so CritterWatch tenant management
  works even without partitioning).
- **`ConjoinedTenantSource : IDynamicTenantSource<string>`**, registered in DI (that
  registration alone lights up CritterWatch's satellite handlers — they resolve and fan out
  over all `IDynamicTenantSource<string>`s, no CritterWatch handler changes):
  - `AddTenantAsync(tenantId)` — registry insert; if partitioned, also
    `AddPartitionToAllTables` (+ per-suffix bucketing). CritterWatch's existing
    auto-assign branch (empty connection string) maps to exactly this call — built for
    Marten sharded tenancy, fits conjoined perfectly;
  - `AddTenantAsync(tenantId, connectionString)` — invalid for conjoined ⇒ clear error
    ("conjoined tenants share the application database");
  - `Disable/Enable` — registry flag; disabled tenants rejected at context-build and
    interceptor time (`UnknownTenantIdException` parity with Marten's master-table
    behavior);
  - `RemoveTenantAsync` — registry delete + (partitioned) partition drop with data removal
    (PG today; SQL Server pending Weasel B1);
  - `FindAsync`/`AllActiveByTenant`/`AllDisabledAsync`/`RefreshAsync` — registry reads;
    the "connection value" returned is the shared app connection (masked in descriptors,
    as `TenantedDbContextUsageSource` already does).
- **Admin convenience API** mirroring Marten:
  `host.AddWolverineManagedTenantsAsync(params string[] tenantIds)` /
  `(Dictionary<string,string> tenantToSuffix)` / `RemoveWolverineManagedTenantsAsync(...)`
  returning Weasel `TablePartitionStatus[]`.
- **Descriptors**: extend `TenantedDbContextUsageSource<T>`/`DbContextUsage` output so the
  conjoined context advertises `DatabaseCardinality.DynamicMultiple` + tenant ids on the
  single `DatabaseDescriptor`.
- **CritterWatch repo work** (separate issues there):
  1. tenant action-strip gating (`hasDynamicTenancy`) currently consults **event-store**
     descriptors only — teach it to consult `DbContextUsage`/document-store descriptors
     with `DynamicMultiple` cardinality;
  2. skip the Postgres-hardcoded `CREATE DATABASE`/`DROP DATABASE` provisioning branches
     when the source is conjoined (no connection string in play; hard-delete for conjoined
     = partition drop + row purge, executed app-side by the source);
  3. Tenants-tab columns already merge per-tenant metrics/DLQ counts keyed by tenant id —
     verify they populate from a conjoined app (single database URI for all rows).

### Phase 4 — Compliance battery, docs, samples

- Compliance-test suite asserting parity with Marten conjoined semantics (shared test list
  reviewed against `conjoined_tenancy_tests` in Marten and Polecat).
- Sample app: `ConjoinedMultiTenantedEfCore` (sibling of `MultiTenantedEfCoreWithPostgreSQL`),
  HTTP tenant detection + partitioning + CritterWatch capabilities.
- Docs: new page under EF Core integration ("Conjoined multi-tenancy and tenant
  partitioning") + blog post announcing the feature (this epic is a strong headline: it is a
  direct, named answer to a community pain point).

---

## Workstream D — Polecat: full Marten per-tenant partitioning parity (follow-up epic)

Today: Polecat partitions **events only** (`UseTenantPartitionedEvents` →
`ManagedTenantPartitions`, per-tenant sequences); `DocumentTable` explicitly throws for
partitioning + conjoined ("RANGE partitioning … single-tenant tables only"); there is no
Marten-style `PartitionMultiTenantedDocumentsUsingMartenManagement` equivalent.

Scope for the follow-up epic (own plan doc when picked up):

1. Managed tenant partitioning for **document tables** (lift the `DocumentTable` restriction;
   `tenant_ordinal` column + shared `ManagedTenantPartitions` feature, one registry per
   database).
2. **Streams-table** partitioning to match Marten (`StreamsTable` takes the same managed
   partitioning as events — audit what `pc_streams` does today).
3. Policy-level API parity: `AllDocumentsAreMultiTenantedWithPartitioning(...)`,
   `PartitionMultiTenantedDocumentsUsingPolecatManagement(...)` naming TBD.
4. Runtime API parity: `store.Advanced.AddPolecatManagedTenantsAsync(...)` /
   `Remove...` with `TablePartitionStatus[]` returns, `__default__`-style reserved partition
   for global projections if the Marten behavior applies.
5. `RemoveTenant` data semantics depend on Weasel B1 (MERGE RANGE leaves rows).
6. CritterWatch: SQL Server hard-delete path is already tracked as CritterWatch#68 — this
   epic is its prerequisite.

Dependencies: Weasel Workstream B first; independent of Wolverine Phases 1–3 (parallel
track).

### Draft issue (polecat repo) — *placeholder wording*

> **Title:** Per-tenant managed partitioning parity with Marten (documents + streams)
> **Body:** Polecat supports Weasel `ManagedTenantPartitions` for the events table only
> (`UseTenantPartitionedEvents`, #163/#171). Marten additionally offers managed tenant
> partitioning for document tables and the streams table, policy APIs
> (`AllDocumentsAreMultiTenantedWithPartitioning`,
> `PartitionMultiTenantedDocumentsUsingMartenManagement`), and runtime tenant onboarding
> (`AddMartenManagedTenantsAsync`). Bring Polecat to parity: lift the DocumentTable
> conjoined-partitioning restriction, partition pc_streams, add the policy + runtime APIs,
> and define tenant-removal data semantics (SQL Server MERGE RANGE does not remove rows —
> see weasel parity issue). Prerequisite for CritterWatch#68 (SQL Server hard delete).

---

## Sequencing & release train

```
A. JasperFx: ITenanted promotion          ──┐  (small; rides next lockstep release)
B. Weasel.SqlServer gap closure           ──┼──> Marten/Polecat pin bumps
                                            │
C1. Wolverine Phase 1 (conjoined core+sagas)│   needs A only
C2. Wolverine Phase 2 (partitioning)        │   needs B (SQL Server), PG ready today
C3. Wolverine Phase 3 (registry + CW)       │   needs C1; CW repo issues in parallel
C4. Wolverine Phase 4 (compliance/docs)     │
                                            │
D. Polecat parity epic                    ──┘   needs B; parallel to C2–C4
```

Phase C1 is independently shippable and already beats the hand-rolled blog approach; C2/C3
are each release-noteworthy on their own.

## Risks / verify-early items

- **EF global query filters + `FindAsync`**: confirm keyed loads respect filters (saga load
  correctness depends on it) — spike in Phase 1 week 1.
- **EF filter caching vs per-instance tenant**: the filter must close over per-context
  state, not a captured value at model-build time — standard pattern, but verify against
  EF Core 10 named filters too (nice interplay: users can *also* declare their own named
  filters without colliding with ours).
- **Composite-PK rewrite (Phase 2)** changes `FindAsync`/`Attach` call shapes for user code
  that loads by key — needs explicit docs + analyzer-grade error messages.
- **SQL Server ordinal stamping** requires `Ordinals` to be loaded before first write per
  tenant — the interceptor needs a synchronous-safe lookup path (pre-hydrated at startup +
  refresh on add), same pattern as Polecat's `TenantEventSequenceRegistry`.
- **Marten + EF conjoined in one app**: both can be conjoined against the same database;
  ensure the two partition control tables (mt_* vs wolverine_*) coexist and CritterWatch
  fans out to both sources without double-adding (its fan-out is by design — document that
  "add tenant" hits both).

## Interview questions for Jeremy (naming/wording — placeholders used above)

1. Registration API name: `AddDbContextWithWolverineManagedConjoinedTenancy<T>` is a
   mouthful. Alternatives: `AddConjoinedMultiTenantedDbContext<T>`,
   `AddDbContextWithConjoinedTenancy<T>`.
2. Registry/control table names: `wolverine_tenants` + `wolverine_tenant_partitions` in the
   durability schema — good, or prefix differently?
3. Partitioned sagas: keep saga id globally unique (simpler frames) or allow per-tenant id
   reuse (composite identity — more work in `DetermineSagaIdType`/load frames)?
4. Exception naming: `CrossTenantWriteException` vs reusing an existing JasperFx type.
5. Should plain-conjoined (no partitioning) also *require* Wolverine-managed migrations, or
   stay EF-migrations-friendly as drafted?
