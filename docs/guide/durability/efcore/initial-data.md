# Initial Data <Badge type="tip" text="5.32" />

`Weasel.EntityFrameworkCore.IInitialData<TContext>` is the declarative seed-data hook that runs every time a `DbContext` is reset via `DatabaseCleaner<T>.ResetAllDataAsync()` or Wolverine's [`host.ResetAllDataAsync<T>()`](./index#resetting-data-between-tests). It's the recommended way to keep integration tests and local dev loops seeded with a known baseline without hand-rolling setup code per test.

See the [Weasel docs on `IInitialData`](https://weasel.jasperfx.net/efcore/database-cleaner.html#iinitialdata) for the authoritative reference — this page is a Wolverine-focused overview of how to use it well.

## Class-based seeders

The traditional form. Implement `Populate`, register with `services.AddInitialData<TContext, TData>()`:

```csharp
public class SeedCoreItems : IInitialData<ItemsDbContext>
{
    public static readonly Item[] Items =
    [
        new Item { Name = "Alpha" },
        new Item { Name = "Beta" }
    ];

    public async Task Populate(ItemsDbContext context, CancellationToken cancellation)
    {
        context.Items.AddRange(Items);
        await context.SaveChangesAsync(cancellation);
    }
}

// Registration
builder.Services.AddInitialData<ItemsDbContext, SeedCoreItems>();
```

## Lambda seeders

For small amounts of seed data, authoring a class is overkill. Weasel 8.14+ exposes a lambda overload:

```csharp
builder.Services.AddInitialData<ItemsDbContext>(async (ctx, ct) =>
{
    ctx.Items.Add(new Item { Name = "Gamma" });
    await ctx.SaveChangesAsync(ct);
});
```

Class-based and lambda seeders can be freely mixed in the same application; they're all resolved as `IEnumerable<IInitialData<TContext>>` at reset time and run in registration order.

## Layered seeders

Because every registered `IInitialData<T>` runs on every reset, you can compose seed data by registering multiple seeders rather than stuffing everything into one class:

```csharp
// Baseline data needed by every test suite.
builder.Services.AddInitialData<ItemsDbContext, SeedCoreItems>();

// Only in Development, add some demo rows.
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddInitialData<ItemsDbContext>(async (ctx, ct) =>
    {
        ctx.Items.Add(new Item { Name = "Dev Demo" });
        await ctx.SaveChangesAsync(ct);
    });
}
```

This is easier to maintain than a single "giant seeder" and plays well with conditional registration (environment, feature flags, tenant).

## Interaction with `ResetAllDataAsync<T>`

`host.ResetAllDataAsync<ItemsDbContext>()` does two things, in order:

1. Delete every row from the tables mapped by `ItemsDbContext` in foreign-key-safe order.
2. Invoke every registered `IInitialData<ItemsDbContext>`.

So the contract of a seeder is: **after me, the database contains whatever I just wrote, plus whatever later seeders write.** If you need the row to exist after `ResetAllDataAsync<T>()`, put it in an `IInitialData<T>`.

## Idempotency

Seeders are expected to be idempotent across resets — the cleaner always deletes first, so you can use fixed primary keys without collision concerns:

```csharp
public async Task Populate(ItemsDbContext context, CancellationToken cancellation)
{
    context.Items.Add(new Item
    {
        Id = Guid.Parse("10000000-0000-0000-0000-000000000001"),
        Name = "Deterministic seed"
    });
    await context.SaveChangesAsync(cancellation);
}
```

This makes assertions trivial: look up the known Id, no need to keep a reference returned from setup.

## When not to use `IInitialData`

- **Production bootstrap data** — `IInitialData` runs on *reset*, not on every app start. For data that should exist on first deploy, use EF Core's [`UseSeeding`](https://learn.microsoft.com/ef/core/modeling/data-seeding) or an explicit setup command.
- **Per-test unique fixtures** — if each test needs fundamentally different baseline data, keep that inside the test (Arrange) rather than baking it into shared seeders. `IInitialData` is for the *common floor* across your suite.
