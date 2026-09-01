# GH-3542 — partitioned conjoined EF Core sagas: PARKED

**Branch:** `gh-3542-partitioned-conjoined-sagas`. **Do not merge.** Two tests are red on purpose;
they are the two that prove the feature works.

Parked 2026-09-01. Everything below is verified against a running PostgreSQL, not read off the source.

## Where it stands

11 of 13 in `conjoined_partitioning_with_postgresql` pass, including the two that used to pin the
v1 exclusion and now pin the new behaviour. The two that fail are the ones that matter:
`two_tenants_can_reuse_one_saga_id_without_colliding` and `a_saga_from_another_tenant_is_not_loaded`.

Both fail the same way — inserting a partitioned saga throws:

```
CrossTenantWriteException : Cannot apply a 'Added' change to an entity of type
PartitionedCounterSaga belonging to tenant '<A GUID>' through a DbContext scoped to tenant 'green'
```

The saga's `TenantId` holds the saga's **id** by the time `SaveChanges` runs.

## Done

- `ConjoinedTenancy.IsPartitionedEntity` — the `!CanBeCastTo<Saga>()` exclusion is gone, so saga
  tables partition and join the managed set. Table creation and `AddTenantAsync` reporting followed
  for free, because both already flowed from this one predicate.
- `ConjoinedTenancy.NeedsCompositeModelKey` — new, and the asymmetry is the whole point: ordinary
  partitioned entities keep the db-only composite (store-generated ids cannot collide), sagas get a
  real one (app-assigned ids can).
- `ConjoinedTenancyModelCustomizer.applyCompositeSagaKeys` — declares `(Id, TenantId)`.
- `LoadEntityFrame` — emits `FindAsync(sagaId, tenantId.Value)` when the key is composite. The
  tenant is reachable at codegen through `TenantIdSource`; that was the feasibility question and it
  is answered.
- Compliance battery updated, plus the two new tests.

## The open problem, and what it is NOT

Established by bisect and instrumentation rather than by reading:

1. **The composite model key is the trigger.** Disable only `applyCompositeSagaKeys`, leave sagas
   partitioned, and both tests pass. So the exclusion removal is fine on its own.
2. **It is not key order.** `(TenantId, Id)` and `(Id, TenantId)` fail identically.
3. **It is not `DetermineSagaIdType`.** That method reads `FindPrimaryKey().GetKeyType()`, which
   stops describing the saga's own identity once the key is composite — a real problem worth keeping
   the fix for — but excluding the tenant property there changed nothing.
4. **No Wolverine code writes the property.** `PartitionedCounterSaga.TenantId`'s setter was
   temporarily instrumented to dump a stack trace on any GUID-valued write. **It never fired.** EF
   writes the backing field directly.

So this is EF populating the tenant key property during its own key fixup, not Wolverine assigning
the wrong value. **Two mechanisms own one property**: the composite key declaration maps
`ITenanted.TenantId` as a key property, and `TenantStampingInterceptor` also writes it.

## Where to start next

Do not re-run the four checks above; they are settled. The question is how to declare the composite
identity so EF does not treat `ITenanted.TenantId` as a key value to populate:

- A shadow key property alongside the CLR one, with the interceptor keeping ownership of the CLR
  property — costs a second column unless mapped to the same one.
- Map the key to the existing tenant column but keep the CLR property out of the key.
- Reconsider whether the real composite has to live in the EF model at all, or whether a db-only
  composite plus an explicit uniqueness guard gets the same safety. Note this reopens the decision
  recorded on the issue, which chose the real composite precisely because a documented-only
  constraint fails silently — so it is a decision to revisit deliberately, not to drift into.

## Not started

`PartitionPerTenant()` on SQL Server was never exercised — only the PostgreSQL half of the battery
was run. Docs still carry the "sagas are not partitioned" caveat.
