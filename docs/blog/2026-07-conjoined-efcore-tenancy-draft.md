# Draft: Conjoined Multi-Tenancy for EF Core, the Critter Stack Way

> DRAFT for Jeremy's edit — announcement post for the GH-3465 epic, targeted at the Wolverine
> 6.21 release. Code samples reference the `ConjoinedMultiTenantedEfCore` sample app.

There's a well-traveled blog-post genre: "how we built shared-database multi-tenancy in EF
Core." A recent, well-written entry in that genre walks through the whole checklist by hand — a
tenant id column on every table, a global query filter everyone on the team has to remember to
configure (and to *not* accidentally bypass with `IgnoreQueryFilters()`), interceptors to stamp
the tenant id on writes, and raw partition DDL smuggled into EF migrations. It works. It's also
a lot of sharp-edged infrastructure code that every team rebuilds slightly differently, where
one forgotten filter means Party A is reading Party B's mail.

Marten users have had all of that as a one-liner ("conjoined tenancy") for a decade. As of
Wolverine 6.21, EF Core users get the same thing:

```csharp
builder.Services.AddDbContextWithWolverineManagedConjoinedTenancy<InvoicingDbContext>(
    (services, opts) => opts.UseNpgsql(connectionString));
```

Mark your entities with `ITenanted` (a marker shared across the whole critter stack from
`JasperFx.MultiTenancy` — Marten and Polecat use the same one):

```csharp
public class Invoice : ITenanted
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = null!;
    // ...
}
```

And Wolverine takes it from there:

- **Mapped `tenant_id` column + composite indexing** — model conventions applied for you, no
  fluent-API boilerplate per entity.
- **Tenant-bound global query filter** — every query through the context is automatically
  scoped to the message's (or HTTP request's) tenant. There is no filter to forget.
- **Stamp-on-insert** — new entities get the ambient tenant id; your handlers never touch
  `TenantId`.
- **Cross-tenant write rejection** — a write to an entity from another tenant throws
  `CrossTenantWriteException` instead of silently corrupting a neighbor's data.
- **Tenant detection you already have** — the same Wolverine HTTP tenant-detection and message
  `TenantId` propagation that all the other Wolverine multi-tenancy features use.
- **Conjoined sagas** — stateful workflows are tenant-scoped too.

## Physical partitioning, if and when you want it

Logical isolation is where most systems start; some end up wanting physical isolation for the
big tables without changing the programming model. The epic ships opt-in **Weasel-managed
tenant partitioning** — PostgreSQL list partitions (with optional bucketing) and SQL Server
tenant-ordinal partitioning — managed as schema objects the way Weasel manages everything else,
not hand-written DDL in a migration. Same entities, same queries, same code.

## An authoritative tenant registry

Tenancy metadata lives in a Wolverine-owned `wolverine_tenants` table: an authoritative list of
tenants (enable/disable included) that doubles as a dynamic tenant source for the rest of
Wolverine — and that CritterWatch surfaces for tenant management out of the box.

## Marten parity, on purpose

The test battery for this feature is a port of Marten's conjoined-tenancy compliance suite —
sentinel values, `TenantIdStyle` handling, stamping, hydration, tenant-scoped deletes,
cross-tenant rejection. If you know how conjoined tenancy behaves in Marten, you know how it
behaves here, because it's checked against the same expectations.

## Where to start

The `ConjoinedMultiTenantedEfCore` sample app in the Wolverine repo is the full tour: tenanted
entities, HTTP tenant detection, stamping and rejection in action, the partitioning opt-in, and
the tenant registry. Docs: [link]. Ships in Wolverine 6.21 with JasperFx 2.30.x, Weasel 9.18.x,
and Marten 9.16.x.
