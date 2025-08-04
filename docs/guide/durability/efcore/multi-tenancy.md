# Multi-Tenancy with EF Core <Badge type="tip" text="4.0" />

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
    opts.PersistMessagesWithPostgresql(configuration.GetConnectionString("main"))

        // Add known tenants at bootstrapping time
        .RegisterStaticTenants(tenants =>
        {
            // Add connection strings for the expected tenant ids
            tenants.Register("tenant1", configuration.GetConnectionString("tenant1"));
            tenants.Register("tenant2", configuration.GetConnectionString("tenant2"));
            tenants.Register("tenant3", configuration.GetConnectionString("tenant3"));
        });
    
    opts.Services.AddDbContextWithWolverineManagedMultiTenancy<ItemsDbContext>((builder, connectionString, _) =>
    {
        builder.UseNpgsql(connectionString.Value, b => b.MigrationsAssembly("MultiTenantedEfCoreWithPostgreSQL"));
    }, AutoCreate.CreateOrUpdate);
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/EfCoreTests/MultiTenancy/MultiTenancyDocumentationSamples.cs#L24-L51' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_static_tenant_registry_with_postgresql' title='Start of snippet'>anchor</a></sup>
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
    opts.PersistMessagesWithSqlServer(configuration.GetConnectionString("main"))

        // Add known tenants at bootstrapping time
        .RegisterStaticTenants(tenants =>
        {
            // Add connection strings for the expected tenant ids
            tenants.Register("tenant1", configuration.GetConnectionString("tenant1"));
            tenants.Register("tenant2", configuration.GetConnectionString("tenant2"));
            tenants.Register("tenant3", configuration.GetConnectionString("tenant3"));
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/EfCoreTests/MultiTenancy/MultiTenancyDocumentationSamples.cs#L56-L90' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_static_tenant_registry_with_sqlserver' title='Start of snippet'>anchor</a></sup>
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
    x.MasterDatabaseConnectionString = Servers.PostgresConnectionString;
});

opts.Services.AddDbContextWithWolverineManagedMultiTenancyByDbDataSource<ItemsDbContext>((builder, dataSource, _) =>
{
    builder.UseNpgsql(dataSource, b => b.MigrationsAssembly("MultiTenantedEfCoreWithPostgreSQL"));
}, AutoCreate.CreateOrUpdate);
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/EfCoreTests/MultiTenancy/multi_tenancy_with_marten_managed_multi_tenancy.cs#L24-L44' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_use_multi_tenancy_with_both_marten_and_ef_core' title='Start of snippet'>anchor</a></sup>
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
    opts.PersistMessagesWithSqlServer(configuration.GetConnectionString("main"))

        // Add known tenants at bootstrapping time
        .RegisterStaticTenants(tenants =>
        {
            // Add connection strings for the expected tenant ids
            tenants.Register("tenant1", configuration.GetConnectionString("tenant1"));
            tenants.Register("tenant2", configuration.GetConnectionString("tenant2"));
            tenants.Register("tenant3", configuration.GetConnectionString("tenant3"));
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/EfCoreTests/MultiTenancy/MultiTenancyDocumentationSamples.cs#L56-L90' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_static_tenant_registry_with_sqlserver' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Then you can _still_ use those EF Core `DbContext` services with Wolverine messaging including the Wolverine outbox like 
this sample code:

<!-- snippet: sample_using_IDbContextOutboxFactory -->
<a id='snippet-sample_using_idbcontextoutboxfactory'></a>
```cs
public class MyMessageHandler
{
    private readonly IDbContextOutboxFactory _factory;

    public MyMessageHandler(IDbContextOutboxFactory factory)
    {
        _factory = factory;
    }

    public async Task HandleAsync(CreateItem command, string tenantId, CancellationToken cancellationToken)
    {
        // Get an EF Core DbContext wrapped in a Wolverine IDbContextOutbox<ItemsDbContext>
        // for message sending wrapped in a transaction spanning the DbContext and Wolverine
        var outbox = await _factory.CreateForTenantAsync<ItemsDbContext>(tenantId, cancellationToken);
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/EfCoreTests/MultiTenancy/MultiTenancyDocumentationSamples.cs#L190-L220' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_idbcontextoutboxfactory' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The important thing to note above is just that this pattern and service will work with any .NET code and not just within Wolverine
handlers or HTTP endpoints. This is your primary mechanism most likely to start transforming an existing AspNetCore system that isn't
using Wolverine.HTTP. 
