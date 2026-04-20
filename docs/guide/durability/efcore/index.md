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

var connectionString = builder.Configuration.GetConnectionString("sqlserver")!;

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/EfCoreTests/SampleUsageWithAutoApplyTransactions.cs#L39-L66' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_getting_started_with_efcore' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Do note that I purposely configured the `ServiceLifetime` of the `DbContextOptions` for our `DbContext` type to be `Singleton`. 
That actually makes a non-trivial performance optimization for Wolverine and how it can treat `DbContext` types at runtime. 

Or alternatively, you can do this in one step with this equivalent approach:

<!-- snippet: sample_idiomatic_wolverine_registration_of_ef_core -->
<a id='snippet-sample_idiomatic_wolverine_registration_of_ef_core'></a>
```cs
var builder = Host.CreateApplicationBuilder();

var connectionString = builder.Configuration.GetConnectionString("sqlserver")!;

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/EfCoreTests/SampleUsageWithAutoApplyTransactions.cs#L72-L91' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_idiomatic_wolverine_registration_of_ef_core' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->



Right now, we've tested Wolverine with EF Core using both [SQL Server](/guide/durability/sqlserver) and [PostgreSQL](/guide/durability/postgresql) persistence. 

## Development-time usage <Badge type="tip" text="5.32" />

Wolverine + EF Core is designed to keep the dev loop short: fast schema iteration, cheap per-test database resets, declarative seed data. The three pillars below all work together — and all come for free the moment you call `UseEntityFrameworkCoreTransactions()`.

### Weasel-managed schema migrations

`UseEntityFrameworkCoreWolverineManagedMigrations()` hands schema management to [Weasel](https://weasel.jasperfx.net/efcore/migrations.html) rather than EF Core's migration-chain tooling. The shape of the story:

| | EF Core migrations | Weasel migrations |
|---|---|---|
| Model | Ordered chain of up/down scripts checked in alongside code | Diff the live database against the current `DbContext` model at startup |
| Authoring | Generate + edit migration classes | Nothing — just change your model |
| Iteration cost | Slow (script regeneration, merge conflicts on parallel branches) | None — restart the app |
| Best for | Production deployments with a change audit | Local dev, integration tests, short-lived branches |

Register it on `WolverineOptions`:

```csharp
builder.UseWolverine(opts =>
{
    opts.Services.AddDbContextWithWolverineIntegration<ItemsDbContext>(
        x => x.UseSqlServer(connectionString));

    // Diff the DbContext against the live DB at startup and apply missing DDL.
    opts.UseEntityFrameworkCoreWolverineManagedMigrations();
});
```

The [Weasel docs](https://weasel.jasperfx.net/efcore/migrations.html) go deeper on the diff engine, opt-outs, and how it handles schemas.

### IInitialData — declarative seed data

Implement `Weasel.EntityFrameworkCore.IInitialData<TContext>` (or register a lambda with `services.AddInitialData<TContext>(...)`) to declare data that should be present every time the database is reset:

```csharp
public class SeedItems : IInitialData<ItemsDbContext>
{
    public async Task Populate(ItemsDbContext context, CancellationToken cancellation)
    {
        context.Items.Add(new Item { Name = "Seed" });
        await context.SaveChangesAsync(cancellation);
    }
}

builder.Services.AddInitialData<ItemsDbContext, SeedItems>();
```

Multiple seeders run in registration order. See the [dedicated page on initial data](./initial-data) for patterns around layered seeders, lambda-based registration, and multi-tenant seeding.

### Resetting data between tests

Two knobs, finest-grained first:

**Per-DbContext — `host.ResetAllDataAsync<T>()`** <Badge type="tip" text="5.32" />

Wipes one `DbContext`'s tables in FK-safe order and reruns that context's `IInitialData<T>` seeders. This is the right default for most integration tests:

```csharp
[Fact]
public async Task ordering_flow()
{
    await _host.ResetAllDataAsync<ItemsDbContext>();

    // arrange ... act ... assert
}
```

The underlying `DatabaseCleaner<T>` is registered automatically by `UseEntityFrameworkCoreTransactions()` — no `services.AddDatabaseCleaner<T>()` needed.

**Global — `host.ResetResourceState()`**

Resets every `IStatefulResource` registered with the host — Wolverine's message store, every broker, every `DbContext` cleaner. Bigger hammer; right when a test writes to multiple stores or you've seen cross-test contamination you can't isolate.

**Recommendation:** use the finest-grained mechanism the test actually needs. Resetting the world on every test multiplies your suite runtime for no benefit.
