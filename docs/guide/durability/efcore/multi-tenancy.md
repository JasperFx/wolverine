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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/EfCoreTests.MultiTenancy/MultiTenancyDocumentationSamples.cs#L24-L50' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_static_tenant_registry_with_postgresql' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/EfCoreTests.MultiTenancy/MultiTenancyDocumentationSamples.cs#L55-L88' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_static_tenant_registry_with_sqlserver' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/EfCoreTests.MultiTenancy/MultiTenancyDocumentationSamples.cs#L55-L88' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_static_tenant_registry_with_sqlserver' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/EfCoreTests.MultiTenancy/MultiTenancyDocumentationSamples.cs#L184-L213' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_idbcontextoutboxfactory' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/EfCoreTests.MultiTenancy/MultiTenancyDocumentationSamples.cs#L247-L262' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_conjoined_tenanted_entity' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/EfCoreTests.MultiTenancy/MultiTenancyDocumentationSamples.cs#L221-L244' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_conjoined_tenancy_with_postgresql' title='Start of snippet'>anchor</a></sup>
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
