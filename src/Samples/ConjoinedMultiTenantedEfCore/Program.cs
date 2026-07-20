using ConjoinedMultiTenantedEfCore.Invoicing;
using ConjoinedMultiTenantedEfCore.Tenants;
using JasperFx;
using JasperFx.Resources;
using Microsoft.EntityFrameworkCore;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Http;
using Wolverine.Postgresql;

// Conjoined EF Core multi-tenancy (GH-3465): many tenants, ONE shared PostgreSQL
// database. Every entity implementing JasperFx.MultiTenancy.ITenanted gets a
// tenant_id column, a tenant-bound global query filter, tenant stamping on
// insert, and cross-tenant write rejection -- all applied by Wolverine, with
// zero tenancy code in the entities, the DbContext, the endpoints, or the
// message handlers.
//
// This is the scenario from
// https://barretblake.dev/posts/development/2026/07/multi-tenant-part-1/
// (hand-rolled shared-database tenancy in EF Core: a tenant column on every
// table, named query filters everyone must remember, raw partition DDL smuggled
// into migrations) -- with every one of those pain points automated away.

var builder = WebApplication.CreateBuilder(args);

// See appsettings.json -- this defaults to the dockerized PostgreSQL from
// Wolverine's own docker-compose file (port 5433):
//
//     docker compose up -d postgresql
var connectionString = builder.Configuration.GetConnectionString("postgres")!;

builder.Services.AddWolverineHttp();

// **2. Registration**
//
// One call opts the InvoicingDbContext into Wolverine-managed conjoined
// tenancy. The DbContext shares the application's Wolverine message store
// database, so you configure a provider but never a connection string here --
// Wolverine hands you the shared database's connection string.
//
// This also registers the IDbContextOutboxFactory, the transactional outbox
// code generation support, and the IDynamicTenantSource<string> tenant
// registry used by the /tenants endpoints
builder.Services.AddDbContextWithWolverineManagedConjoinedTenancy<InvoicingDbContext>(
    (options, connection) => options.UseNpgsql(connection.Value),

    // Create the invoicing schema objects (and apply model changes) on startup
    AutoCreate.CreateOrUpdate

    // **6. OPTIONAL: Weasel-managed physical partitioning**
    //
    // Uncomment the option below and every non-saga ITenanted entity table is
    // physically partitioned per tenant -- PostgreSQL LIST partitions on
    // tenant_id, managed by Weasel through the wolverine_tenant_partitions
    // control table. No hand-written partition DDL hidden inside EF migrations.
    // Partitions are created/dropped through the tenant registry (see the
    // /tenants endpoints) or the IConjoinedTenantPartitions<InvoicingDbContext>
    // service, which also supports sharing one partition between small tenants.
    //
    // Requires UseEntityFrameworkCoreWolverineManagedMigrations() below, since
    // EF migrations cannot express the partition DDL.
    //
    // , tenancy => tenancy.PartitionPerTenant()
);

builder.Host.UseWolverine(opts =>
{
    // The Wolverine message store IS the shared application database for
    // conjoined tenancy. Using durable PostgreSQL storage gives this app the
    // transactional inbox/outbox *and* the wolverine_tenants registry table
    opts.PersistMessagesWithPostgresql(connectionString, "wolverine");

    // EF Core backs Wolverine's transactional middleware...
    opts.UseEntityFrameworkCoreTransactions();

    // ...and Wolverine/Weasel manage the schema instead of EF migrations.
    // This is what lets AutoCreate.CreateOrUpdate above build the invoicing
    // tables, and it's required if you opt into PartitionPerTenant()
    opts.UseEntityFrameworkCoreWolverineManagedMigrations();

    // Wrap every handler and HTTP endpoint that writes through a DbContext in
    // a transaction that spans the entity work and the outgoing messages
    opts.Policies.AutoApplyTransactions();

    // The InvoiceCreated message cascaded from POST /invoices is processed on a
    // durable (inbox-backed) local queue
    opts.Policies.UseDurableLocalQueues();

    // Build out the database schema (message store + invoicing tables) on startup
    opts.Services.AddResourceSetupOnStartup();

    // Demo convenience: seed the "acme" and "initech" tenants in the
    // wolverine_tenants registry. Registered AFTER AddResourceSetupOnStartup so
    // the registry table exists before the seeder runs
    opts.Services.AddHostedService<TenantSeeder>();
});

builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// **2 (continued). HTTP tenant detection**
//
// Wolverine.Http detects the tenant id from each request and flows it through
// the endpoint, the DbContext, and any cascaded messages. The endpoints
// themselves never look at headers or query strings
#region sample_conjoined_http_tenant_detection
app.MapWolverineEndpoints(opts =>
{
    // Try headers first...
    opts.TenantId.IsRequestHeaderValue("tenant-id");

    // ...then fall back to a query string value, e.g. GET /invoices?tenant=acme
    opts.TenantId.IsQueryStringValue("tenant");

    // Any tenanted endpoint called without a detectable tenant id gets a 400
    // with ProblemDetails instead of quietly running against the default
    // tenant. The /tenants administrative endpoints opt out with [NotTenanted]
    opts.TenantId.AssertExists();
});
#endregion

return await app.RunJasperFxCommands(args);
