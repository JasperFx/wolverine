# Entity Framework Core Integration

Wolverine supports [Entity Framework Core](https://learn.microsoft.com/en-us/ef/core/) through the `WolverineFx.EntityFrameworkCore` Nuget.

* Transactional middleware - Wolverine will both call `DbContext.SaveChangesAsync()` and flush any persisted messages for you
* EF Core as a saga storage mechanism - As long as one of your registered `DbContext` services has a mapping for the stateful saga type
* Outbox integration - Wolverine can use directly use a `DbContext` that has mappings for the Wolverine durable messaging, or at least use the database connection and current database transaction from a `DbContext` as part of durable, outbox message persistence.
* [Multi-Tenancy with EF Core](./multi-tenancy)

## Getting Started

The first step is to just install the `WolverineFx.EntityFrameworkCore` Nuget:

```bash
dotnet add package WolverineFx.EntityFrameworkCore
```

::: warning
For right now, it's perfectly possible to use multiple `DbContext` types with one Wolverine application and Wolverine
is perfectly capable of using the correct `DbContext` type for `Saga` types. **But**, Wolverine can only use the transactional
inbox/outbox with a single database registration. This limitation will be lifted later as folks are going to eventually hit
this limitation with modular monolith approaches.
:::

With that in place, there's two basic things you need in order to fully use EF Core with Wolverine as shown below:

<!-- snippet: sample_getting_started_with_efcore -->
<a id='snippet-sample_getting_started_with_efcore'></a>
```cs
var builder = Host.CreateApplicationBuilder();

var connectionString = builder.Configuration.GetConnectionString("sqlserver");

// Register a DbContext or multiple DbContext types as normal
builder.Services.AddDbContext<SampleDbContext>(
    x => x.UseSqlServer(connectionString), 
    
    // This is actually a significant performance gain
    // for Wolverine's sake
    optionsLifetime:ServiceLifetime.Singleton);

// Register Wolverine
builder.UseWolverine(opts =>
{
    // You'll need to independently tell Wolverine where and how to 
    // store messages as part of the transactional inbox/outbox
    opts.PersistMessagesWithSqlServer(connectionString);
    
    // Adding EF Core transactional middleware, saga support,
    // and EF Core support for Wolverine storage operations
    opts.UseEntityFrameworkCoreTransactions();
});

// Rest of your bootstrapping...
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/EfCoreTests/SampleUsageWithAutoApplyTransactions.cs#L40-L68' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_getting_started_with_efcore' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Do note that I purposely configured the `ServiceLifetime` of the `DbContextOptions` for our `DbContext` type to be `Singleton`. 
That actually makes a non-trivial performance optimization for Wolverine and how it can treat `DbContext` types at runtime. 

Or alternatively, you can do this in one step with this equivalent approach:

<!-- snippet: sample_idiomatic_wolverine_registration_of_ef_core -->
<a id='snippet-sample_idiomatic_wolverine_registration_of_ef_core'></a>
```cs
var builder = Host.CreateApplicationBuilder();

var connectionString = builder.Configuration.GetConnectionString("sqlserver");

builder.UseWolverine(opts =>
{
    // You'll need to independently tell Wolverine where and how to 
    // store messages as part of the transactional inbox/outbox
    opts.PersistMessagesWithSqlServer(connectionString);
    
    // Registers the DbContext type in your IoC container, sets the DbContextOptions
    // lifetime to "Singleton" to optimize Wolverine usage, and also makes sure that
    // your Wolverine service has all the EF Core transactional middleware, saga support,
    // and storage operation helpers activated for this application
    opts.Services.AddDbContextWithWolverineIntegration<SampleDbContext>(
        x => x.UseSqlServer(connectionString));
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/EfCoreTests/SampleUsageWithAutoApplyTransactions.cs#L74-L94' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_idiomatic_wolverine_registration_of_ef_core' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->



Right now, we've tested Wolverine with EF Core using both [SQL Server](/guide/durability/sqlserver) and [PostgreSQL](/guide/durability/postgresql) persistence. 
