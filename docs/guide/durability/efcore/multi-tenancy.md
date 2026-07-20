# Multi-Tenancy with EF Core <Badge type="tip" text="4.0" />

::: tip
For a holistic overview of multi-tenancy across all of Wolverine, see the [Multi-Tenancy Tutorial](/tutorials/multi-tenancy).
For additional context on the EF Core multi-tenancy story, see the blog post
[Wolverine 4 is Bringing Multi-Tenancy to EF Core](https://jeremydmiller.com/2025/05/15/wolverine-4-is-bringing-multi-tenancy-to-ef-core/).
:::

Wolverine has first class support for using a single EF Core `DbContext` type that potentially uses different databases
for different clients within your system, and this includes every single bit of EF Core capabilities with Wolverine:

* Wolverine will manage a separate transactional inbox & outbox for each tenant database and any main database
* The transactional middleware is multi-tenant aware for EF Core
* Wolverine's [Tenant id detection for HTTP](/guide/http/multi-tenancy.html#tenant-id-detection) is supported by the EF Core integration
* The [storage actions](/guide/durability/efcore/operations) and `[Entity]` attribute support for EF Core will respect the multi-tenancy

Alright, let's get into a first concrete sample. In this simplest usage, I'm assuming that there are only three separate
tenant databases, and each database will only hold data for a single tenant. 

To use EF Core with [multi-tenanted PostgreSQL](/guide/durability/postgresql.html#multi-tenancy) storage, we can use this:

<!-- snippet: sample_static_tenant_registry_with_postgresql -->
<a id='snippet-sample_static_tenant_registry_with_postgresql'></a>
```cs
var builder = Host.CreateApplicationBuilder();

var configuration = builder.Configuration;

builder.UseWolverine(opts =>
{
    // First, you do have to have a "main" PostgreSQL database for messaging persistence
    // that will store information about running nodes, agents, and non-tenanted operations
    opts.PersistMessagesWithPostgresql(configuration.GetConnectionString("main")!)

        // Add known tenants at bootstrapping time
        .RegisterStaticTenants(tenants =>
        {
            // Add connection strings for the expected tenant ids
            tenants.Register("tenant1", configuration.GetConnectionString("tenant1")!);
            tenants.Register("tenant2", configuration.GetConnectionString("tenant2")!);
            tenants.Register("tenant3", configuration.GetConnectionString("tenant3")!);
        });
    
    opts.Services.AddDbContextWithWolverineManagedMultiTenancy<ItemsDbContext>((builder, connectionString, _) =>
    {
        builder.UseNpgsql(connectionString.Value, b => b.MigrationsAssembly("MultiTenantedEfCoreWithPostgreSQL"));
    }, AutoCreate.CreateOrUpdate);
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/EfCoreTests.MultiTenancy/MultiTenancyDocumentationSamples.cs#L25-L51' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_static_tenant_registry_with_postgresql' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

And instead with [multi-tenanted SQL Server](/guide/durability/sqlserver.html#multi-tenancy) storage:

<!-- snippet: sample_static_tenant_registry_with_sqlserver -->
<a id='snippet-sample_static_tenant_registry_with_sqlserver'></a>
```cs
var builder = Host.CreateApplicationBuilder();

var configuration = builder.Configuration;

builder.UseWolverine(opts =>
{
    // First, you do have to have a "main" PostgreSQL database for messaging persistence
    // that will store information about running nodes, agents, and non-tenanted operations
    opts.PersistMessagesWithSqlServer(configuration.GetConnectionString("main")!)

        // Add known tenants at bootstrapping time
        .RegisterStaticTenants(tenants =>
        {
            // Add connection strings for the expected tenant ids
            tenants.Register("tenant1", configuration.GetConnectionString("tenant1")!);
            tenants.Register("tenant2", configuration.GetConnectionString("tenant2")!);
            tenants.Register("tenant3", configuration.GetConnectionString("tenant3")!);
        });
    
    // Just to show that you *can* use more than one DbContext
    opts.Services.AddDbContextWithWolverineManagedMultiTenancy<ItemsDbContext>((builder, connectionString, _) =>
    {
        // You might have to set the migration assembly
        builder.UseSqlServer(connectionString.Value, b => b.MigrationsAssembly("MultiTenantedEfCoreWithSqlServer"));
    }, AutoCreate.CreateOrUpdate);

    opts.Services.AddDbContextWithWolverineManagedMultiTenancy<OrdersDbContext>((builder, connectionString, _) =>
    {
        builder.UseSqlServer(connectionString.Value, b => b.MigrationsAssembly("MultiTenantedEfCoreWithSqlServer"));
    }, AutoCreate.CreateOrUpdate);
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/EfCoreTests.MultiTenancy/MultiTenancyDocumentationSamples.cs#L56-L89' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_static_tenant_registry_with_sqlserver' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Note in both samples how I'm registering the `DbContext` types. There's a fluent interface first to register the multi-tenanted
database storage, then a call to register a `DbContext` with multi-tenancy. You'll have to supply Wolverine with a lambda
to configure the `DbContextOptionsBuilder` for the individual `DbContext` object. At runtime, Wolverine will be passing in the right
connection string for the active tenant id. There is also other overloads to configure based on a `DbDataSource` if using
PostgreSQL or to also take in a `TenantId` value type that will give you the active tenant id if you need to use that
for setting EF Core query filters like [this example from the Microsoft documentation](https://learn.microsoft.com/en-us/ef/core/miscellaneous/multitenancy#an-example-solution-single-database).

## Combine with Marten

It's perfectly possible to use [Marten](https://martendb.io) and its multi-tenancy support for targeting a separate database
with EF Core using the same databases. Maybe you're using Marten for event sourcing, then using EF Core for flat table projections.
Regardless, you simply allow Marten to manage the multi-tenancy and the relationship between tenant ids and the various databases,
and the Wolverine EF Core integration can more or less ride on Marten's coat tails:

<!-- snippet: sample_use_multi_tenancy_with_both_marten_and_ef_core -->
<a id='snippet-sample_use_multi_tenancy_with_both_marten_and_ef_core'></a>
```cs
opts.Services.AddMarten(m =>
{
    m.MultiTenantedDatabases(x =>
    {
        x.AddSingleTenantDatabase(tenant1ConnectionString, "red");
        x.AddSingleTenantDatabase(tenant2ConnectionString, "blue");
        x.AddSingleTenantDatabase(tenant3ConnectionString, "green");
    });
}).IntegrateWithWolverine(x =>
{
    x.MainDatabaseConnectionString = Servers.PostgresConnectionString;
});

opts.Services.AddDbContextWithWolverineManagedMultiTenancyByDbDataSource<ItemsDbContext>((builder, dataSource, _) =>
{
    builder.UseNpgsql(dataSource, b => b.MigrationsAssembly("MultiTenantedEfCoreWithPostgreSQL"));
}, AutoCreate.CreateOrUpdate);
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/EfCoreTests.MultiTenancy/multi_tenancy_with_marten_managed_multi_tenancy.cs#L24-L43' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_use_multi_tenancy_with_both_marten_and_ef_core' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Outside of Handlers or Endpoints

It's a complex world full of legacy systems and existing codebases, and it's quite possible you're going to want to publish messages to Wolverine
from outside of Wolverine HTTP endpoints or Wolverine message handlers where there is no clean transactional middleware approach to just do
the outbox and multi-tenancy mechanics for you. Not to worry, you can still leverage Wolverine's EF Core integration with both multi-tenancy and
the Wolverine outbox sending with the `IDbContextOutboxFactory` service.

Let's say that you have a relatively simple multi-tenancy setup with SQL Server and EF Core `DbContext` services like this:

<!-- snippet: sample_static_tenant_registry_with_sqlserver -->
<a id='snippet-sample_static_tenant_registry_with_sqlserver'></a>
```cs
var builder = Host.CreateApplicationBuilder();

var configuration = builder.Configuration;

builder.UseWolverine(opts =>
{
    // First, you do have to have a "main" PostgreSQL database for messaging persistence
    // that will store information about running nodes, agents, and non-tenanted operations
    opts.PersistMessagesWithSqlServer(configuration.GetConnectionString("main")!)

        // Add known tenants at bootstrapping time
        .RegisterStaticTenants(tenants =>
        {
            // Add connection strings for the expected tenant ids
            tenants.Register("tenant1", configuration.GetConnectionString("tenant1")!);
            tenants.Register("tenant2", configuration.GetConnectionString("tenant2")!);
            tenants.Register("tenant3", configuration.GetConnectionString("tenant3")!);
        });
    
    // Just to show that you *can* use more than one DbContext
    opts.Services.AddDbContextWithWolverineManagedMultiTenancy<ItemsDbContext>((builder, connectionString, _) =>
    {
        // You might have to set the migration assembly
        builder.UseSqlServer(connectionString.Value, b => b.MigrationsAssembly("MultiTenantedEfCoreWithSqlServer"));
    }, AutoCreate.CreateOrUpdate);

    opts.Services.AddDbContextWithWolverineManagedMultiTenancy<OrdersDbContext>((builder, connectionString, _) =>
    {
        builder.UseSqlServer(connectionString.Value, b => b.MigrationsAssembly("MultiTenantedEfCoreWithSqlServer"));
    }, AutoCreate.CreateOrUpdate);
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/EfCoreTests.MultiTenancy/MultiTenancyDocumentationSamples.cs#L56-L89' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_static_tenant_registry_with_sqlserver' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Then you can _still_ use those EF Core `DbContext` services with Wolverine messaging including the Wolverine outbox like 
this sample code:

<!-- snippet: sample_using_idbcontextoutboxfactory -->
<a id='snippet-sample_using_idbcontextoutboxfactory'></a>
```cs
public class MyMessageHandler
{
    private readonly IDbContextOutboxFactory _factory;

    public MyMessageHandler(IDbContextOutboxFactory factory)
    {
        _factory = factory;
    }

    public async Task HandleAsync(CreateItem command, TenantId tenantId, CancellationToken cancellationToken)
    {
        // Get an EF Core DbContext wrapped in a Wolverine IDbContextOutbox<ItemsDbContext>
        // for message sending wrapped in a transaction spanning the DbContext and Wolverine
        var outbox = await _factory.CreateForTenantAsync<ItemsDbContext>(tenantId.Value, cancellationToken);
        var item = new Item { Name = command.Name, Id = CombGuidIdGeneration.NewGuid() };

        outbox.DbContext.Items.Add(item);
        
        // Don't worry, this messages doesn't *actually* get sent until
        // the transaction succeeds
        await outbox.PublishAsync(new ItemCreated { Id = item.Id });

        // Save and commit the unit of work with the outgoing message,
        // then "flush" the outgoing messages through Wolverine
        await outbox.SaveChangesAndFlushMessagesAsync(cancellationToken);
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/EfCoreTests.MultiTenancy/MultiTenancyDocumentationSamples.cs#L185-L214' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_idbcontextoutboxfactory' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The important thing to note above is just that this pattern and service will work with any .NET code and not just within Wolverine
handlers or HTTP endpoints. This is your primary mechanism most likely to start transforming an existing AspNetCore system that isn't
using Wolverine.HTTP. 

## Conjoined Multi-Tenancy <Badge type="tip" text="6.21" />

::: tip
Conjoined tenancy is the same model Marten calls ["conjoined" multi-tenancy](https://martendb.io/documents/multi-tenancy.html) —
one shared database and schema, with each row tagged and filtered by a `tenant_id` column. The `ITenanted` marker interface is
shared across the whole Critter Stack from `JasperFx.MultiTenancy`, so the exact same marker drives conjoined behavior in
Marten, Polecat, and Wolverine's EF Core integration.
:::

The database-per-tenant model above isn't the right fit for every system. If you want all tenants in a **single, shared
database** — one connection string, one set of tables, a `tenant_id` discriminator column — use Wolverine's *conjoined*
multi-tenancy for EF Core. Marking an entity with `JasperFx.MultiTenancy.ITenanted` is all it takes:

<!-- snippet: sample_conjoined_tenanted_entity -->
<a id='snippet-sample_conjoined_tenanted_entity'></a>
```cs
// Implementing the JasperFx.MultiTenancy.ITenanted interface --
// the same marker interface Marten uses for conjoined tenancy --
// opts this entity into Wolverine's conjoined multi-tenancy
public class TenantedItem : ITenanted
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;

    // Wolverine maps, stamps, and hydrates this for you. Treat the
    // value as framework-managed
    public string? TenantId { get; set; }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/EfCoreTests.MultiTenancy/MultiTenancyDocumentationSamples.cs#L304-L319' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_conjoined_tenanted_entity' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

and registering the `DbContext` with conjoined tenancy:

<!-- snippet: sample_conjoined_tenancy_with_postgresql -->
<a id='snippet-sample_conjoined_tenancy_with_postgresql'></a>
```cs
var builder = Host.CreateApplicationBuilder();

var configuration = builder.Configuration;

builder.UseWolverine(opts =>
{
    // One single database for messaging persistence *and*
    // all tenanted application data
    opts.PersistMessagesWithPostgresql(configuration.GetConnectionString("main")!);

    // Conjoined multi-tenancy: every entity implementing
    // JasperFx.MultiTenancy.ITenanted is mapped with a tenant_id column,
    // filtered by the current tenant on every query, stamped with the
    // ambient tenant id on inserts, and guarded against cross-tenant
    // updates and deletes
    opts.Services.AddDbContextWithWolverineManagedConjoinedTenancy<ConjoinedTenancy.ConjoinedItemsDbContext>(
        (builder, connectionString) =>
        {
            builder.UseNpgsql(connectionString.Value);
        }, AutoCreate.CreateOrUpdate);
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/EfCoreTests.MultiTenancy/MultiTenancyDocumentationSamples.cs#L222-L245' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_conjoined_tenancy_with_postgresql' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

With that registration, Wolverine takes over all the mechanical multi-tenancy chores you would otherwise hand-roll
with EF Core query filters:

* Every `ITenanted` entity is mapped with a `tenant_id` column (defaulted to Wolverine's `*DEFAULT*` tenant sentinel) and an index on that column
* A global query filter binds every query to the tenant of the current message or HTTP request — there are no named filters for your team to remember, and no "one forgotten `IgnoreQueryFilters()`" data leakage from ad hoc LINQ
* On `SaveChanges`, inserted entities are stamped with the ambient tenant id (after any `TenantIdStyle` correction)
* Updates or deletes against an entity belonging to a *different* tenant throw `CrossTenantWriteException` instead of quietly crossing tenant boundaries
* Sagas implementing `ITenanted` get tenant-scoped loads — the same saga id in two different tenants are two different sagas as far as loading is concerned
* All of Wolverine's existing tenant id detection (message `TenantId`, [HTTP tenant detection](/guide/http/multi-tenancy.html#tenant-id-detection), `InvokeForTenantAsync()`) flows through unchanged

Because conjoined tenancy is a single database, the messaging storage is just the plain, non-tenanted message store —
there's no per-tenant inbox/outbox to manage, and the transactional middleware and outbox work exactly as they do in
a single-tenant application.

Note that a `DbContext` type registered with conjoined tenancy is pinned to the tenant id of the message being handled
at the time it's created. If you need to query across tenants for administrative functions, use `IgnoreQueryFilters()`
in your LINQ queries — but remember that the write-side guards will still stop you from modifying another tenant's data
through a tenant-pinned `DbContext`.

### A Worked Example <Badge type="tip" text="6.21" />

::: tip
The complete, runnable version of everything below — HTTP tenant detection, seeded tenants, a guided `curl` tour, and the
optional partitioning switch — is the [`ConjoinedMultiTenantedEfCore` sample application](https://github.com/JasperFx/wolverine/tree/main/src/Samples/ConjoinedMultiTenantedEfCore).
:::

The whole point of conjoined tenancy is that your *application* code stops carrying tenancy plumbing. Start with an
ordinary entity — the only tenancy-related thing about it is the `ITenanted` marker — alongside an entity that is
deliberately left non-tenanted so it stays shared across every tenant:

<!-- snippet: sample_conjoined_invoice_entity -->
<a id='snippet-sample_conjoined_invoice_entity'></a>
```cs
public class Invoice : ITenanted
{
    public Guid Id { get; set; }
    public string Description { get; set; } = null!;
    public decimal Amount { get; set; }
    public InvoiceStatus Status { get; set; } = InvoiceStatus.Pending;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Wolverine maps, stamps, and hydrates this for you. Treat the
    // value as framework-managed
    public string? TenantId { get; set; }
}

// Deliberately NOT ITenanted. Entities that don't implement the marker are left
// completely alone -- no tenant_id column, no query filter, no guard. Perfect
// for reference data shared by every tenant (think a common product catalog)
public class Product
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
    public decimal ListPrice { get; set; }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/ConjoinedMultiTenantedEfCore/Invoicing/Invoice.cs#L27-L50' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_conjoined_invoice_entity' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The `DbContext` is completely vanilla. There is no `tenant_id` mapping, no `HasQueryFilter()` to remember for each new
entity, no `SaveChanges` override, and no interceptor — Wolverine's model customizer applies all of that for you:

<!-- snippet: sample_conjoined_vanilla_dbcontext -->
<a id='snippet-sample_conjoined_vanilla_dbcontext'></a>
```cs
public class InvoicingDbContext : DbContext
{
    public InvoicingDbContext(DbContextOptions<InvoicingDbContext> options) : base(options)
    {
    }

    public DbSet<Invoice> Invoices { get; set; } = null!;
    public DbSet<Product> Products { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Invoice>(map =>
        {
            map.ToTable("invoices", "invoicing");
            map.HasKey(x => x.Id);
        });

        modelBuilder.Entity<Product>(map =>
        {
            map.ToTable("products", "invoicing");
            map.HasKey(x => x.Id);
        });
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/ConjoinedMultiTenantedEfCore/Invoicing/InvoicingDbContext.cs#L15-L40' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_conjoined_vanilla_dbcontext' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Detect the tenant once, where you configure Wolverine's HTTP endpoints. From here on nothing in your endpoints or
handlers ever looks at a header, a query string, or `TenantId`:

<!-- snippet: sample_conjoined_http_tenant_detection -->
<a id='snippet-sample_conjoined_http_tenant_detection'></a>
```cs
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
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/ConjoinedMultiTenantedEfCore/Program.cs#L112-L126' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_conjoined_http_tenant_detection' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

A write endpoint just adds the entity. It never reads a header, never sets `TenantId`, and never calls
`SaveChangesAsync()` — the tenant stamping interceptor supplies the tenant id and the [EF Core transactional
middleware](/guide/durability/efcore/transactional-middleware) commits both the row and the cascaded message through the durable outbox:

<!-- snippet: sample_conjoined_stamp_on_insert_endpoint -->
<a id='snippet-sample_conjoined_stamp_on_insert_endpoint'></a>
```cs
[WolverinePost("/invoices")]
public static (CreationResponse<InvoiceCreated>, InvoiceCreated) Create(
    CreateInvoice command,
    InvoicingDbContext db)
{
    var invoice = new Invoice
    {
        Id = Guid.NewGuid(),
        Description = command.Description,
        Amount = command.Amount
    };

    db.Invoices.Add(invoice);

    var created = new InvoiceCreated(invoice.Id, invoice.Amount);
    return (CreationResponse.For(created, $"/invoices/{invoice.Id}"), created);
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/ConjoinedMultiTenantedEfCore/Invoicing/InvoiceEndpoints.cs#L29-L47' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_conjoined_stamp_on_insert_endpoint' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Read endpoints are just as clean. There is no `Where(x => x.TenantId == ...)` anywhere — the global query filter binds
every query (and `FindAsync()`) to the detected tenant, so calling as `acme` can only ever see `acme`'s rows:

<!-- snippet: sample_conjoined_tenant_scoped_query -->
<a id='snippet-sample_conjoined_tenant_scoped_query'></a>
```cs
[WolverineGet("/invoices")]
public static Task<Invoice[]> GetAll(InvoicingDbContext db)
{
    return db.Invoices.OrderBy(x => x.CreatedAt).ToArrayAsync();
}

// FindAsync respects the tenant filter as well -- asking for another
// tenant's invoice id returns null, which Wolverine.Http turns into a 404
[WolverineGet("/invoices/{id}")]
public static Task<Invoice?> GetById(Guid id, InvoicingDbContext db)
{
    return db.Invoices.FindAsync(id).AsTask();
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/ConjoinedMultiTenantedEfCore/Invoicing/InvoiceEndpoints.cs#L55-L69' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_conjoined_tenant_scoped_query' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The `InvoiceCreated` message cascaded from that write endpoint carries the tenant id on its envelope, so a message
handler running later on a durable local queue — completely outside the original HTTP request — is tenant-scoped in
exactly the same way, with the same zero plumbing:

<!-- snippet: sample_conjoined_tenant_scoped_handler -->
<a id='snippet-sample_conjoined_tenant_scoped_handler'></a>
```cs
public static class InvoiceCreatedHandler
{
    // Toy business rule: small invoices are approved automatically
    public const decimal AutoApprovalLimit = 500;

    public static async Task Handle(InvoiceCreated message, InvoicingDbContext db, ILogger logger)
    {
        // Tenant-scoped load -- a message for tenant "acme" can never touch
        // an "initech" invoice, even though both live in the same table
        var invoice = await db.Invoices.FindAsync(message.InvoiceId);
        if (invoice == null)
        {
            return;
        }

        if (invoice.Amount <= AutoApprovalLimit)
        {
            invoice.Status = InvoiceStatus.Approved;
            logger.LogInformation("Auto-approved invoice {InvoiceId} for tenant {TenantId}",
                invoice.Id, invoice.TenantId);
        }
        else
        {
            logger.LogInformation("Invoice {InvoiceId} for tenant {TenantId} needs manual approval",
                invoice.Id, invoice.TenantId);
        }
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/ConjoinedMultiTenantedEfCore/Invoicing/InvoiceCreatedHandler.cs#L16-L45' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_conjoined_tenant_scoped_handler' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Finally, the write-side guard. Even if application code deliberately smuggles another tenant's row out with
`IgnoreQueryFilters()`, modifying it is rejected at `SaveChanges` time with `CrossTenantWriteException` before anything
reaches the database:

<!-- snippet: sample_conjoined_cross_tenant_write_rejection -->
<a id='snippet-sample_conjoined_cross_tenant_write_rejection'></a>
```cs
public static class CrossTenantWriteDemo
{
    [WolverinePost("/demos/cross-tenant-write")]
    public static async Task<CrossTenantWriteAttempted> Attempt(HijackInvoice command, InvoicingDbContext db)
    {
        // IgnoreQueryFilters() is the "one forgotten filter" from the motivating
        // blog post, weaponized: it lets us see (and track) rows from every tenant
        var smuggled = await db.Invoices.IgnoreQueryFilters()
            .SingleOrDefaultAsync(x => x.Id == command.InvoiceId);
        if (smuggled == null)
        {
            return new CrossTenantWriteAttempted(false,
                $"No invoice with id {command.InvoiceId} exists for any tenant");
        }

        smuggled.Description = command.NewDescription;

        try
        {
            await db.SaveChangesAsync();

            // Only reachable when the invoice already belongs to the calling tenant
            return new CrossTenantWriteAttempted(false,
                "The write succeeded because the invoice belongs to the calling tenant. " +
                "Call this endpoint again with a different tenant-id header to see the rejection.");
        }
        catch (CrossTenantWriteException e)
        {
            // Nothing was written. Clear the poisoned change tracker so the
            // transactional middleware's own SaveChangesAsync stays a no-op
            db.ChangeTracker.Clear();

            return new CrossTenantWriteAttempted(true, e.Message, e.EntityTenantId, e.ContextTenantId);
        }
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/ConjoinedMultiTenantedEfCore/Demos/CrossTenantWriteDemo.cs#L28-L65' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_conjoined_cross_tenant_write_rejection' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

### Tenant Partitioning <Badge type="tip" text="6.21" />

Opt into Weasel-managed **partition-per-tenant** physical partitioning with `PartitionPerTenant()`:

<!-- snippet: sample_conjoined_tenancy_with_partitioning -->
<a id='snippet-sample_conjoined_tenancy_with_partitioning'></a>
```cs
opts.Services.AddDbContextWithWolverineManagedConjoinedTenancy<ConjoinedTenancy.ConjoinedItemsDbContext>(
    (builder, connectionString) => builder.UseNpgsql(connectionString.Value),
    AutoCreate.CreateOrUpdate,

    // Weasel-managed physical partitioning: one partition (or shared
    // bucket) per tenant on every non-saga ITenanted entity table
    tenancy => tenancy.PartitionPerTenant());
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/EfCoreTests.MultiTenancy/MultiTenancyDocumentationSamples.cs#L257-L265' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_conjoined_tenancy_with_partitioning' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

With partitioning enabled:

* On PostgreSQL, every non-saga `ITenanted` entity table becomes `PARTITION BY LIST (tenant_id)` with one partition per tenant, managed through a `wolverine_tenant_partitions` control table in the durability schema
* On SQL Server (which can only range-partition over a compact value), entities gain an `int tenant_ordinal` column stamped automatically by Wolverine, and tables are `RANGE RIGHT` partitioned over the ordinal with a registry table mapping tenant ids to ordinals
* The composite `(tenant, id)` primary key exists **only in the database** — your EF model keeps its own single key, so `FindAsync()`, `Attach()`, and saga loads keep exactly the same call shapes
* Multiple small tenants can share one physical partition ("bucketing") by registering them with the same partition suffix — the answer to SQL Server's partition count ceiling and to "small tenants don't deserve their own partition"
* Partitioned conjoined contexts require `UseEntityFrameworkCoreWolverineManagedMigrations()` — EF migrations cannot express the partition DDL

Manage tenants through `IConjoinedTenantPartitions<TDbContext>`:

<!-- snippet: sample_conjoined_partitioning_tenant_management -->
<a id='snippet-sample_conjoined_partitioning_tenant_management'></a>
```cs
var partitions = host.Services
    .GetRequiredService<IConjoinedTenantPartitions<ConjoinedTenancy.ConjoinedItemsDbContext>>();

// Each tenant gets its own physical partition
await partitions.AddTenantAsync("tenant1");

// Or share one partition between small tenants ("bucketing") --
// requires AllowPartitionSharing on the partitioning options
await partitions.AddTenantAsync("small-tenant-a", "shared_bucket");
await partitions.AddTenantAsync("small-tenant-b", "shared_bucket");

// Dropping a tenant's partition removes its rows
await partitions.DropTenantAsync("tenant1", deleteData: true);
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/EfCoreTests.MultiTenancy/MultiTenancyDocumentationSamples.cs#L271-L285' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_conjoined_partitioning_tenant_management' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Note that with partitioning enabled, a tenant's partition must exist before rows can be written for that tenant.
Sagas are deliberately **not** partitioned in this release — they keep the conjoined query filtering and tenant
stamping, but stay in unpartitioned tables so saga identity is untouched.

### The Tenant Registry <Badge type="tip" text="6.21" />

Conjoined registrations keep an authoritative tenant list in the `wolverine_tenants` table in the durability schema —
the same table Wolverine's master-table tenancy uses, with an empty connection string marking a shared-database tenant.
The registry is exposed through JasperFx's `IDynamicTenantSource<string>`, which is also what lights up tenant
management from [CritterWatch](https://critterwatch.io):

<!-- snippet: sample_conjoined_tenant_registry -->
<a id='snippet-sample_conjoined_tenant_registry'></a>
```cs
var tenants = host.Services.GetRequiredService<IDynamicTenantSource<string>>();

// Registers the tenant in wolverine_tenants (and creates its
// partitions when partitioning is enabled)
await tenants.AddTenantAsync("tenant1", CancellationToken.None);

// Soft delete: the tenant's data stays, but writes are rejected
await tenants.DisableTenantAsync("tenant1");
await tenants.EnableTenantAsync("tenant1");

// Hard delete: registry record removed; with partitioning enabled the
// tenant's partition is dropped along with its rows
await tenants.RemoveTenantAsync("tenant1");
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/EfCoreTests.MultiTenancy/MultiTenancyDocumentationSamples.cs#L287-L301' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_conjoined_tenant_registry' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Disabled tenants are rejected at `SaveChanges` time with `UnknownTenantIdException`. Removing a tenant deletes its
registry record, and — when partitioning is enabled — drops the tenant's partition *including its rows*.
