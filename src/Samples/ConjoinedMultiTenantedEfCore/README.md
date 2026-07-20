# ConjoinedMultiTenantedEfCore

Sample application for **Wolverine-managed conjoined EF Core multi-tenancy**
([GH-3465](https://github.com/JasperFx/wolverine/issues/3465)): many tenants
sharing **one** PostgreSQL database, where every entity implementing
`JasperFx.MultiTenancy.ITenanted` is automatically:

* mapped with a `tenant_id` column (plus index)
* filtered by the current tenant through a global query filter on every query
* stamped with the ambient tenant id on insert
* guarded against cross-tenant updates and deletes (`CrossTenantWriteException`)

This mirrors the motivating scenario from
[Barret Blake's multi-tenancy series](https://barretblake.dev/posts/development/2026/07/multi-tenant-part-1/) —
hand-rolled shared-database tenancy in EF Core with a tenant column on every
table, query filters everyone must remember, and raw partition DDL smuggled
into EF migrations. Wolverine automates every one of those pain points, with the
same conjoined-tenancy semantics Marten has always had.

## What to look at

| Concern | File |
|---|---|
| `ITenanted` entity, vanilla `DbContext` | `Invoicing/Invoice.cs`, `Invoicing/InvoicingDbContext.cs` |
| Registration + HTTP tenant detection | `Program.cs` |
| Stamp-on-insert, tenant-scoped HTTP queries | `Invoicing/InvoiceEndpoints.cs` |
| Tenant-scoped message handler | `Invoicing/InvoiceCreatedHandler.cs` |
| Cross-tenant write rejection | `Demos/CrossTenantWriteDemo.cs` |
| Opt-in per-tenant physical partitioning | commented option in `Program.cs` |
| `wolverine_tenants` registry | `Tenants/TenantEndpoints.cs` |

## Running it

```bash
# from the wolverine repo root: dockerized PostgreSQL on port 5433
docker compose up -d postgresql

dotnet run --framework net9.0 --project src/Samples/ConjoinedMultiTenantedEfCore
```

The app listens on `http://localhost:5581` and seeds two fictional tenants,
`acme` and `initech`, in the `wolverine_tenants` registry at startup.

## A guided tour with curl

```bash
# The authoritative tenant registry (wolverine_tenants table)
curl http://localhost:5581/tenants

# Create an invoice as acme -- note the endpoint code never touches TenantId,
# and small invoices get auto-approved by a tenant-scoped message handler
curl -s -X POST http://localhost:5581/invoices \
  -H 'content-type: application/json' -H 'tenant-id: acme' \
  -d '{"description": "widgets", "amount": 120}'

# acme sees its invoice...
curl -s 'http://localhost:5581/invoices?tenant=acme'

# ...initech sees nothing, same table, no Where() clauses anywhere
curl -s 'http://localhost:5581/invoices?tenant=initech'

# Forgetting the tenant entirely is a 400, not a data leak
curl -s http://localhost:5581/invoices

# Try to hijack acme's invoice as initech (use the id from the create call):
# the write is rejected with CrossTenantWriteException before touching the db
curl -s -X POST http://localhost:5581/demos/cross-tenant-write \
  -H 'content-type: application/json' -H 'tenant-id: initech' \
  -d '{"invoiceId": "<invoice-id>", "newDescription": "hijacked!"}'

# Tenant lifecycle: disable (soft), enable, remove (hard)
curl -s -X POST http://localhost:5581/tenants/globex
curl -s -X POST http://localhost:5581/tenants/globex/disable
curl -s -X DELETE http://localhost:5581/tenants/globex
```

## Physical partitioning (optional)

Uncomment `tenancy => tenancy.PartitionPerTenant()` in `Program.cs` and every
non-saga `ITenanted` table becomes PostgreSQL LIST-partitioned per tenant,
managed by Weasel through the `wolverine_tenant_partitions` control table —
adding/removing tenants through the `/tenants` endpoints creates/drops the
partitions. No partition DDL in your migrations. (Drop the `invoicing` schema
first when toggling this on an existing database — a plain table can't be
converted to a partitioned one in place.)
