# Database Migrations

Wolverine uses [Weasel](https://github.com/JasperFx/weasel) for schema management of EF Core `DbContext` types rather than EF Core's own migration system. This approach provides a consistent schema management experience across the entire "critter stack" (Wolverine + Marten) and avoids issues with EF Core's `Database.EnsureCreatedAsync()` bypassing migration history.

## How It Works

When you register a `DbContext` with Wolverine using `AddDbContextWithWolverineIntegration<T>()` or call `UseEntityFrameworkCoreWolverineManagedMigrations()`, Wolverine will:

1. **Read the EF Core model** — Wolverine inspects your `DbContext`'s entity types, properties, and relationships to build a Weasel schema representation
2. **Compare against the actual database** — Weasel connects to the database and compares the expected schema with the current state
3. **Apply deltas** — Only the necessary changes (new tables, added columns, foreign keys) are applied

This all happens automatically at application startup when you use `UseResourceSetupOnStartup()` or through Wolverine's resource management commands.

## Enabling Weasel-Managed Migrations

To opt into Weasel-managed migrations for your EF Core `DbContext` types, add this to your Wolverine configuration:

```csharp
builder.UseWolverine(opts =>
{
    opts.PersistMessagesWithSqlServer(connectionString);

    opts.Services.AddDbContextWithWolverineIntegration<MyDbContext>(
        x => x.UseSqlServer(connectionString));

    // Enable Weasel-managed migrations for all registered DbContext types
    opts.UseEntityFrameworkCoreWolverineManagedMigrations();
});
```

With this in place, Wolverine will create and update your EF Core tables using Weasel at startup, alongside any Wolverine envelope storage tables.

## What Gets Migrated

Weasel will manage the following schema elements from your EF Core model:

- **Tables** — Created from entity types registered in `DbSet<T>` properties
- **Columns** — Mapped from entity properties, including types, nullability, and default values
- **Primary keys** — Derived from `DbContext` key configuration
- **Foreign keys** — Including cascade delete behavior
- **Schema names** — Respects EF Core's `ToSchema()` configuration

Entity types excluded from migrations via EF Core's `ExcludeFromMigrations()` are also excluded from Weasel management.

## Programmatic Migration

You can also trigger migrations programmatically using the Weasel extension methods on `IServiceProvider`:

```csharp
// Create a migration plan for a specific DbContext
await using var migration = await serviceProvider
    .CreateMigrationAsync(dbContext, CancellationToken.None);

// Apply the migration (only applies if there are actual differences)
await migration.ExecuteAsync(AutoCreate.CreateOrUpdate, CancellationToken.None);
```

The `CreateMigrationAsync()` method compares the EF Core model against the actual database schema and produces a `DbContextMigration` object. Calling `ExecuteAsync()` applies any necessary changes.

### Creating the Database

If you need to ensure the database itself exists (not just the tables), use:

```csharp
await serviceProvider.EnsureDatabaseExistsAsync(dbContext);
```

This uses Weasel's provider-specific database creation logic, which only creates the database catalog — it does not create any tables or schema objects.

## Multi-Tenancy

For multi-tenant setups where each tenant has its own database, Wolverine will automatically ensure each tenant database exists and apply schema migrations when using the tenanted `DbContext` builder. See [Multi-Tenancy](./multi-tenancy) for details.

## Weasel vs EF Core Migrations

| Feature | Weasel (Wolverine) | EF Core Migrations |
|---------|-------------------|-------------------|
| Migration tracking | Compares live schema | Migration history table |
| Code generation | None needed | `dotnet ef migrations add` |
| Additive changes | Automatic | Requires new migration |
| Works with Marten | Yes, unified approach | No |
| Rollback support | No | Yes, via `Down()` method |

::: tip
Weasel migrations are **additive** — they can create tables and add columns, but will not drop columns or tables automatically. This makes them safe for `CreateOrUpdate` scenarios in production.
:::

::: warning
If you are already using EF Core's migration system (`dotnet ef migrations add`, `Database.MigrateAsync()`), you should choose one approach or the other. Mixing EF Core migrations with Weasel-managed migrations can lead to conflicts. Wolverine's Weasel-managed approach is recommended for applications in the "critter stack" ecosystem.
:::

## CLI Commands

When Weasel-managed migrations are enabled, you can use Wolverine's built-in resource management:

```bash
# Apply all pending schema changes
dotnet run -- resources setup

# Check current database status
dotnet run -- resources list

# Reset all state (development only!)
dotnet run -- resources clear
```

These commands manage both Wolverine's internal tables and your EF Core entity tables together.
