# Batch Queries / Futures <Badge type="tip" text="5.32" />

Wolverine's EF Core integration can collapse multiple related `SELECT`s inside one handler into a single database round-trip, using [Weasel's `BatchedQuery`](https://weasel.jasperfx.net/efcore/batch-queries.html) (the EF Core counterpart to Marten's [batched query](https://martendb.io/documents/querying/batched-queries.html)) as the underlying mechanism.

This page is Wolverine-focused: handler patterns, auto-batching, what the code generator does for you. For the underlying `BatchedQuery` fluent API, see [Weasel's batch-queries guide](https://weasel.jasperfx.net/efcore/batch-queries.html).

## Why batch?

Every extra `SELECT` in a handler is another round-trip to the database. Four queries against a local SQL Server, same handler, four small rows:

| Strategy | Total (50 iterations) | Per handler | Relative |
|---|---|---|---|
| Sequential `await`s | 345.8 ms | **6.92 ms** | 1.0× |
| Batched via `BatchedQuery` | 124.3 ms | **2.49 ms** | **2.78×** |

Measured locally against `mcr.microsoft.com/mssql/server`, 4 keyed lookups per iteration, after warm-up. The speedup scales with (a) number of queries in the handler and (b) network latency — across a region-to-region hop, a four-query handler can drop from ~40 ms to ~12 ms.

## Pattern 1 — Two `IQueryPlan`s on one handler

The simplest win. If your handler [uses query plans](./query-plans) that implement *both* `IQueryPlan<TDbContext, TResult>` **and** `IBatchQueryPlan<TDbContext, TResult>` (which `QueryPlan<TDb, TEntity>` and `QueryListPlan<TDb, TEntity>` do automatically), Wolverine's code generator detects multiple batch-capable loads on the same handler and rewrites them into a shared `BatchedQuery`:

```csharp
public class ShipmentOverview
{
    public Customer Customer { get; set; } = null!;
    public IReadOnlyList<Order> RecentOrders { get; set; } = [];
}

public record GetShipmentOverview(Guid CustomerId);

public static class ShipmentOverviewHandler
{
    public static async Task<ShipmentOverview> Handle(
        GetShipmentOverview query,
        CustomerById customerSpec,          // IQueryPlan + IBatchQueryPlan via QueryPlan<>
        RecentOrdersFor ordersSpec)         //         "          "           QueryListPlan<>
    {
        return new ShipmentOverview
        {
            Customer = await customerSpec.FetchAsync(...),
            RecentOrders = await ordersSpec.FetchAsync(...)
        };
    }
}
```

Wolverine's `EFCoreBatchingPolicy` inspects the handler chain, detects the two batch-capable plans, and generates code equivalent to:

```csharp
// Generated (simplified)
var batch = db.CreateBatchQuery();
var customerTask = customerSpec.FetchAsync(batch, db);
var ordersTask   = ordersSpec.FetchAsync(batch, db);
await batch.ExecuteAsync(cancellation);
var customer = await customerTask;
var orders   = await ordersTask;
```

One round-trip, no manual batch wiring, no change to the plan classes.

## Pattern 2 — Manual `IBatchQueryPlan<TDbContext, TResult>`

When a query doesn't fit the `QueryPlan<>` / `QueryListPlan<>` shape — for example, a projection into a DTO or a pre-aggregated count — implement `IBatchQueryPlan` directly:

```csharp
public class OrderCountFor(Guid customerId) : IBatchQueryPlan<OrderDbContext, int>
{
    public Task<int> FetchAsync(BatchedQuery batch, OrderDbContext db)
        => batch.QueryCount(db.Orders.Where(x => x.CustomerId == customerId));
}
```

Any handler parameter implementing `IBatchQueryPlan<TDb, TResult>` gets the same auto-batching treatment as `IQueryPlan`-derived plans. Use `IBatchQueryPlan` when you need full control over the batched shape; use `QueryPlan<>` / `QueryListPlan<>` when you want the plan to work both standalone and batched.

## Pattern 3 — `[Entity]` primary-key lookup + a spec in the same handler

`[Entity]` lookups and query plans batch together. A common shape — load the root aggregate by id, then fetch a related collection with a spec:

```csharp
public record ArchiveOrderLines(Guid OrderId);

public static class ArchiveOrderLinesHandler
{
    public static async Task Handle(
        ArchiveOrderLines cmd,
        [Entity] Order order,             // PK lookup — batch-capable
        ActiveLinesFor linesSpec,         // QueryListPlan — batch-capable
        OrderDbContext db)
    {
        order.Archive();
        foreach (var line in await linesSpec.FetchAsync(...))
        {
            line.Archive();
        }
        await db.SaveChangesAsync();
    }
}
```

Both the `[Entity]` load and the spec fetch are enlisted into a single `BatchedQuery`, one round-trip for two reads.

## Pattern 4 — Count + page in one round-trip

Paginated list responses almost always want `(totalCount, currentPage)`. Two queries, same filter, one logical operation. Keep it one round-trip:

```csharp
public record OrderSearch(string? Customer, int Page, int PageSize);

public class OrderSearchCount(string? customer) : IBatchQueryPlan<OrderDbContext, int>
{
    public Task<int> FetchAsync(BatchedQuery batch, OrderDbContext db)
        => batch.QueryCount(OrdersMatching(db, customer));
}

public class OrderSearchPage(string? customer, int page, int pageSize)
    : IBatchQueryPlan<OrderDbContext, IReadOnlyList<Order>>
{
    public async Task<IReadOnlyList<Order>> FetchAsync(BatchedQuery batch, OrderDbContext db)
    {
        var list = await batch.Query(
            OrdersMatching(db, customer)
                .OrderByDescending(x => x.PlacedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize));
        return list.ToList();
    }
}

static IQueryable<Order> OrdersMatching(OrderDbContext db, string? customer)
    => customer is null
        ? db.Orders
        : db.Orders.Where(x => x.Customer.Name.Contains(customer));

public static class OrderSearchHandler
{
    public static async Task<(int Total, IReadOnlyList<Order> Page)> Handle(
        OrderSearch query,
        OrderSearchCount countPlan,
        OrderSearchPage pagePlan)
    {
        return (await countPlan.FetchAsync(...), await pagePlan.FetchAsync(...));
    }
}
```

One `SELECT COUNT(*)` + one paginated `SELECT`, merged into a single `DbBatch` on the wire.

## Opting out

If a specific handler genuinely needs sequential round-trips (e.g. because a later query depends on an earlier query's result), just don't use batch-capable plans — the batching policy only fires when two or more `IBatchQueryPlan` parameters appear on the same handler.

## Testing batched handlers

The batching is transparent to tests. The same handler that uses `IQueryPlan` parameters can be unit-tested against an in-memory `DbContext` the normal way — query plans execute standalone through `FetchAsync(DbContext)` when invoked outside a `BatchedQuery`. See [Query Plans → Testing a plan in isolation](./query-plans#testing-a-plan-in-isolation).

## Further reading

- [Weasel — `BatchedQuery` fluent API reference](https://weasel.jasperfx.net/efcore/batch-queries.html)
- [Query Plans](./query-plans) — the Specification model that backs the auto-batching
- [Marten — Batched Queries](https://martendb.io/documents/querying/batched-queries.html) — the Critter Stack sibling this mirrors
