# Wolverine.Marten vs Wolverine.Polecat — Difference Report

## Overview

**Wolverine.Marten** (PostgreSQL via Marten) has ~60 source files. **Wolverine.Polecat** (SQL Server via Polecat) has ~35 source files. Polecat covers the core event sourcing, outbox, and aggregate handler workflow but does not yet include several advanced Marten features.

## Test Coverage Summary

After porting all applicable MartenTests:
- **171 total tests** in PolecatTests (163 passing, 8 skipped due to known gaps)
- **Skipped tests** all relate to `StronglyTypedId` code generation support (7 tests) and empty stream key validation (1 test)

---

## Functionality Present in Both

| Feature | Marten Class | Polecat Class |
|---------|-------------|---------------|
| Aggregate handler workflow | `AggregateHandlerAttribute` | `AggregateHandlerAttribute` |
| Aggregate handling codegen | `AggregateHandling` | `AggregateHandling` |
| Boundary model (DCB) | `BoundaryModelAttribute` | `BoundaryModelAttribute` |
| Consistent aggregate attrs | `ConsistentAggregateAttribute`, `ConsistentAggregateHandlerAttribute` | Same |
| Read/Write aggregate attrs | `ReadAggregateAttribute`, `WriteAggregateAttribute` | Same |
| Updated aggregate return | `UpdatedAggregate<T>` | Same |
| Concurrency styles | `ConcurrencyStyle` | Same |
| Document ops (IMartenOp) | `IMartenOp`, `MartenOps` | `IPolecatOp`, `PolecatOps` |
| Outbox interface | `IMartenOutbox` | `IPolecatOutbox` |
| Envelope transaction | `MartenEnvelopeTransaction` | `PolecatEnvelopeTransaction` |
| Integration bootstrap | `MartenIntegration` | `PolecatIntegration` |
| Session factory | `OutboxedSessionFactory` | `OutboxedSessionFactory` |
| Persistence frame provider | `MartenPersistenceFrameProvider` | `PolecatPersistenceFrameProvider` |
| Session codegen | `OpenMartenSessionFrame`, `CreateDocumentSessionFrame` | Same pattern |
| Event store frame | `EventStoreFrame` | Same |
| Load aggregate frame | `LoadAggregateFrame` | Same |
| Load boundary frame | `LoadBoundaryFrame` | Same |
| Register events frame | `RegisterEventsFrame` | Same |
| Missing aggregate check | `MissingAggregateCheckFrame` | Same |
| Session variable source | `SessionVariableSource` | Same |
| Saga persistence | `LoadDocumentFrame`, `DocumentSessionOperationFrame` | Same |
| Flush messages on commit | `FlushOutgoingMessagesOnCommit` | Same |
| Publish events before commit | `PublishIncomingEventsBeforeCommit` | Same |
| Storage extensions | `MartenStorageExtensions` | `PolecatStorageExtensions` |

---

## Functionality Missing from Wolverine.Polecat

### 1. Agent-Based Event Distribution (4 files)
- `Distribution/EventStoreAgents.cs`
- `Distribution/EventSubscriptionAgent.cs`
- `Distribution/EventSubscriptionAgentFamily.cs`
- `Distribution/WolverineProjectionCoordinator.cs`

**Impact:** Cannot use Wolverine's agent framework to distribute event processing across nodes. This is a major Marten feature for scaling event projections.

### 2. Event Subscription Integration (9 files)
- `Subscriptions/IWolverineSubscription.cs`
- `Subscriptions/BatchSubscription.cs`
- `Subscriptions/InlineInvoker.cs`
- `Subscriptions/InnerDataInvoker.cs`
- `Subscriptions/NulloMessageInvoker.cs`
- `Subscriptions/PublishingRelay.cs`
- `Subscriptions/ScopedWolverineSubscriptionRunner.cs`
- `Subscriptions/WolverineCallbackForCascadingMessages.cs`
- `Subscriptions/WolverineSubscriptionRunner.cs`

**Impact:** Cannot subscribe to Polecat event streams from within Wolverine. Marten provides `SubscribeToEvents()`, `ProcessEventsWithWolverineHandlersInStrictOrder()`, `PublishEventsToWolverine()` — none of these exist for Polecat.

### 3. Multi-Tenant Message Database (2 files)
- `MartenMessageDatabaseSource.cs`
- `AncillaryWolverineOptionsMartenExtensions.cs`

**Impact:** Cannot use database-per-tenant message storage or ancillary Marten document stores.

### 4. Marten Batch Query Codegen (1 file)
- `Codegen/MartenQueryingFrame.cs`

**Impact:** Marten uses `IBatchedQuery` to combine multiple entity loads into a single roundtrip. Polecat does not have this optimization, which affects `[Entity]` attribute performance when loading multiple entities.

### 5. Publishing / Outbox Internals (2 files)
- `Publishing/MartenToWolverineOutbox.cs`
- `Publishing/MartenToWolverineMessageBatch.cs`

**Impact:** Marten implements `IMessageOutbox` / `IMessageBatch` for its outbox pattern. Polecat uses a different approach via session listeners.

### 6. Envelope Persistence Operations (2 files)
- `Persistence/Operations/StoreIncomingEnvelope.cs`
- `Persistence/Operations/StoreOutgoingEnvelope.cs`

**Impact:** Marten has dedicated envelope storage operations. Polecat consolidates this into `PolecatStorageExtensions`.

### 7. Testing Utilities (2 files)
- `MartenTestingExtensions.cs` — `SaveInMartenAndWaitForOutgoingMessagesAsync()`
- `TestingExtensions.cs` — `DocumentStore()` extension on IHost

**Impact:** No convenience extension methods for testing Polecat-backed Wolverine apps. Users must use `host.Services.GetRequiredService<IDocumentStore>()` manually.

### 8. Store-Specific Attributes (1 file)
- `MartenStoreAttribute.cs`

**Impact:** Cannot annotate handler parameters to target a specific named Marten store.

### 9. Requirements System (1 file)
- `Requirements/IDataRequirement.cs`

---

## Known Polecat Code Generation Gaps

These surfaced during test porting:

1. **StronglyTypedId support** — Polecat's code generation emits `LoadAsync<T>(strongId)` without converting to the underlying Guid value. This fails at runtime for:
   - `[Entity]` attribute with StronglyTypedId document identifiers
   - Saga identifiers using StronglyTypedId
   - `[ReadAggregate]` / `[WriteAggregate]` with StronglyTypedId (aggregate handler tests pass, suggesting this path is handled differently)

2. **Empty stream key validation** — `PolecatOps.StartStream<T>()` (no stream key) does not throw `InvalidOperationException` when executed, unlike `MartenOps.StartStream<T>()`.

---

## API Configuration Differences

### Marten
```csharp
services.AddMarten(connString).IntegrateWithWolverine();
services.AddMarten(connString).IntegrateWithWolverine(cfg => cfg.MessageStorageSchemaName = "wolverine");
services.AddMarten(connString).IntegrateWithWolverine<TAncillary>();
services.AddMarten(connString).SubscribeToEvents(subscription);
services.AddMarten(connString).ProcessEventsWithWolverineHandlersInStrictOrder(...);
services.AddMarten(connString).PublishEventsToWolverine(...);
```

### Polecat
```csharp
services.AddPolecat(m => { m.ConnectionString = connStr; }).IntegrateWithWolverine();
services.AddPolecat(m => { m.ConnectionString = connStr; }).IntegrateWithWolverine(cfg => ...);
// No ancillary store, subscription, or event publishing extensions
```

---

## Recommendations for Porting Priority

### High Priority (Core Feature Parity)
1. **Testing extensions** — Small effort, high value for adoption
2. **StronglyTypedId code generation** — Needed for modern C# patterns
3. **Empty stream key validation** — Simple defensive check

### Medium Priority (Advanced Patterns)
4. **Batch query optimization** — Performance improvement for multi-entity handlers
5. **Event subscription integration** — Enables reactive event processing
6. **Publishing outbox internals** — May improve outbox reliability

### Lower Priority (Enterprise Features)
7. **Agent-based distribution** — Complex, needed for multi-node event processing
8. **Multi-tenant message database** — Complex, enterprise-specific
9. **Ancillary store support** — Marten-specific pattern, may not map to Polecat
