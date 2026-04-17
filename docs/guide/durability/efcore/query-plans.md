# Query Plans <Badge type="tip" text="5.32" />

Wolverine.EntityFrameworkCore provides a first-class implementation of the
[Specification pattern](https://specification.ardalis.com/) called a *query plan*,
adapted from Marten's [IQueryPlan](https://martendb.io/documents/querying/compiled-queries.html#query-plans)
and consistent with it across the Critter Stack.

A query plan is a reusable, testable unit of query logic that encapsulates a
LINQ query over a `DbContext`. Handlers can consume complex reads without
reaching for a repository/adapter layer — a middle ground between
`[Entity]` (primary-key lookup only) and a bespoke repository service.

## Why query plans?

In real-world codebases, handlers frequently need queries more complex than a
primary-key lookup, but not complex enough to justify an adapter layer. Query
plans give you:

- **Reusability** — one query class, many callers
- **Testability** — instantiate and call `FetchAsync` against an in-memory
  `DbContext`; no host, no mocking
- **Composability** — parameters flow through the constructor; plans can be
  combined inside a handler
- **No magic** — no source generators, no DI registration, no runtime
  reflection. A plan is just a class with a `Query()` method.

## Defining a plan

Inherit from `QueryPlan<TDbContext, TEntity>` for a single result, or
`QueryListPlan<TDbContext, TEntity>` for a list:

```csharp
using Wolverine.EntityFrameworkCore;

public class ActiveOrderForCustomer(Guid customerId) : QueryPlan<OrderDbContext, Order>
{
    public override IQueryable<Order> Query(OrderDbContext db)
        => db.Orders
            .Where(x => x.CustomerId == customerId && !x.IsArchived)
            .OrderByDescending(x => x.CreatedAt);
}

public class OrdersForCustomer(Guid customerId) : QueryListPlan<OrderDbContext, Order>
{
    public override IQueryable<Order> Query(OrderDbContext db)
        => db.Orders
            .Where(x => x.CustomerId == customerId)
            .Include(x => x.LineItems)
            .OrderBy(x => x.CreatedAt);
}
```

Everything LINQ-to-EF supports — `Include`, `OrderBy`, `Select`, `Skip`,
`Take`, projection into DTOs — works inside `Query()`.

## Using a plan in a handler

The simplest pattern: inject your `DbContext` into the handler and execute the
plan against it.

```csharp
public static async Task Handle(
    ApproveOrder msg,
    OrderDbContext db,
    CancellationToken ct)
{
    var order = await db.QueryByPlanAsync(
        new ActiveOrderForCustomer(msg.CustomerId), ct);

    if (order is null) throw new InvalidOperationException("No active order");

    order.Approve();
    // DbContext.SaveChangesAsync() is invoked automatically by Wolverine's
    // EF Core transactional middleware.
}
```

`QueryByPlanAsync` is a convenience extension on `DbContext`. Equivalent to
calling `plan.FetchAsync(db, ct)` directly.

## Testing a plan in isolation

Because plans have no framework dependencies, you can unit-test them with EF
Core's in-memory provider (or a real SQLite in-memory connection) without
starting a Wolverine host:

```csharp
[Fact]
public async Task active_order_plan_finds_most_recent_unarchived_order()
{
    var options = new DbContextOptionsBuilder<OrderDbContext>()
        .UseInMemoryDatabase($"plan-test-{Guid.NewGuid():N}")
        .Options;

    await using var db = new OrderDbContext(options);
    var customerId = Guid.NewGuid();
    db.Orders.AddRange(
        new Order { CustomerId = customerId, IsArchived = true,  CreatedAt = DateTime.UtcNow.AddDays(-2) },
        new Order { CustomerId = customerId, IsArchived = false, CreatedAt = DateTime.UtcNow.AddHours(-1) });
    await db.SaveChangesAsync();

    var plan = new ActiveOrderForCustomer(customerId);
    var result = await plan.FetchAsync(db, default);

    result.ShouldNotBeNull();
    result.IsArchived.ShouldBeFalse();
}
```

## When to reach for a query plan

| Situation                                             | Recommendation                |
| ----------------------------------------------------- | ----------------------------- |
| Load one entity by primary key                        | `[Entity]` attribute          |
| Load by a simple predicate, used in one handler       | Inline LINQ is fine           |
| Reusable query used across multiple handlers          | **Query plan**                |
| Complex query with projection/paging/caching rules    | **Query plan**                |
| Cross-aggregate read model with data shaping          | **Query plan** or a dedicated query service |

## Relationship to Marten

This is the same shape as Marten's `IQueryPlan<T>` (see the
[Marten docs](https://martendb.io/documents/querying/compiled-queries.html#query-plans))
with the signature tweaked for EF Core's `DbContext`. If you are using both
Marten and EF Core in a Critter Stack application, plans on both sides read
identically.

## Relationship to Ardalis.Specification

The programming model — a class that encapsulates query logic, parameters via
constructor, composition of `Where`/`Include`/`OrderBy` — matches
[Ardalis.Specification](https://specification.ardalis.com/). The key
difference is that query plans expose the raw `IQueryable<T>` builder (so any
LINQ operator EF Core supports is available) rather than a curated DSL, and
integrate directly with Wolverine's handler pipeline.
