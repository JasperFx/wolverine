# Transactional Middleware

Support for using Wolverine transactional middleware requires an explicit registration on `WolverineOptions`
shown below (it's an extension method):

<!-- snippet: sample_registering_efcore_middleware -->
<a id='snippet-sample_registering_efcore_middleware'></a>
```cs
builder.Host.UseWolverine(opts =>
{
    // Setting up Sql Server-backed message storage
    // This requires a reference to Wolverine.SqlServer
    opts.PersistMessagesWithSqlServer(connectionString!, "wolverine");

    // Set up Entity Framework Core as the support
    // for Wolverine's transactional middleware
    opts.UseEntityFrameworkCoreTransactions();

    // Enrolling all local queues into the
    // durable inbox/outbox processing
    opts.Policies.UseDurableLocalQueues();
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/EFCoreSample/ItemService/Program.cs#L50-L66' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_registering_efcore_middleware' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

::: tip
When using the opt in `Handlers.AutoApplyTransactions()` option, Wolverine can detect that your handler method uses a `DbContext` if it's a method argument,
a dependency of any service injected as a method argument, or a dependency of any service injected as a constructor
argument of the handler class.
:::

That will enroll EF Core as both a strategy for stateful saga support and for transactional middleware. With this
option added, Wolverine will wrap transactional middleware around any message handler that has a dependency on any
type of `DbContext` like this one:

<!-- snippet: sample_handler_using_efcore -->
<a id='snippet-sample_handler_using_efcore'></a>
```cs
[Transactional]
public static ItemCreated Handle(
    // This would be the message
    CreateItemCommand command,

    // Any other arguments are assumed
    // to be service dependencies
    ItemsDbContext db)
{
    // Create a new Item entity
    var item = new Item
    {
        Name = command.Name
    };

    // Add the item to the current
    // DbContext unit of work
    db.Items.Add(item);

    // This event being returned
    // by the handler will be automatically sent
    // out as a "cascading" message
    return new ItemCreated
    {
        Id = item.Id
    };
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/EFCoreSample/ItemService/CreateItemCommandHandler.cs#L7-L36' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_handler_using_efcore' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

When using the transactional middleware around a message handler, the `DbContext` is used to persist
the outgoing messages as part of Wolverine's outbox support.

### Opting Out with [NonTransactional]

When using `AutoApplyTransactions()`, you can opt specific handlers or HTTP endpoints out of
transactional middleware by decorating them with the `[NonTransactional]` attribute:

```cs
using Wolverine.Attributes;

public static class MyHandler
{
    // This handler will NOT have transactional middleware applied
    // even when AutoApplyTransactions() is enabled
    [NonTransactional]
    public static void Handle(MyCommand command, MyDbContext db)
    {
        // You're managing the DbContext yourself here
    }
}
```

The `[NonTransactional]` attribute can be placed on individual handler methods or on the handler class to opt out all methods in that class.

## Eager vs Lightweight Transactions <Badge type="tip" text="5.15" />

By default, the EF Core middleware will run in `Eager` mode meaning that Wolverine
will call `DbContext.Database.BeginTransactionAsync()` before your message handler or HTTP
endpoint handler. We do this so that bulk operations can succeed. If all you need to do is
persist entities such that `DbContext.SaveChangesAsync()` gives you all the transactional integrity
you need, you can opt into lightweight transaction code generation instead:

<!-- snippet: sample_using_lightweight_ef_core_transactions -->
<a id='snippet-sample_using_lightweight_ef_core_transactions'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.Durability.Mode = DurabilityMode.Solo;

        opts.Services.AddDbContextWithWolverineIntegration<CleanDbContext>(x =>
            x.UseSqlServer(Servers.SqlServerConnectionString));

        opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "txmode");
        
        // ONLY use SaveChangesAsync() for transaction boundaries
        // Treat the DbContext as a unit of work, assume there are no
        // bulk operations
        opts.UseEntityFrameworkCoreTransactions(TransactionMiddlewareMode.Lightweight);
        opts.Policies.AutoApplyTransactions();

        opts.Discovery.DisableConventionalDiscovery()
            .IncludeType<LightweightModeHandler>();
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/EfCoreTests/transaction_middleware_mode_tests.cs#L51-L72' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_lightweight_ef_core_transactions' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

You can also selectively configure the transaction middleware mode on singular message handlers or HTTP endpoints
with the `[Transactional]` attribute like this:

<!-- snippet: sample_explicit_usage_of_transaction_middleware_mode -->
<a id='snippet-sample_explicit_usage_of_transaction_middleware_mode'></a>
```cs
public class LightweightAttributeHandler
{
    [Transactional(Mode = TransactionMiddlewareMode.Lightweight)]
    public static void Handle(LightweightAttributeMessage message, CleanDbContext db)
    {
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/EfCoreTests/transaction_middleware_mode_tests.cs#L270-L279' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_explicit_usage_of_transaction_middleware_mode' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Auto Apply Transactional Middleware

You can opt into automatically applying the transactional middleware to any handler that depends on a `DbContext` type
with the `AutoApplyTransactions()` option as shown below:

<!-- snippet: sample_bootstrapping_with_auto_apply_transactions_for_sql_server -->
<a id='snippet-sample_bootstrapping_with_auto_apply_transactions_for_sql_server'></a>
```cs
var builder = Host.CreateApplicationBuilder();
builder.UseWolverine(opts =>
{
    var connectionString = builder.Configuration.GetConnectionString("database");

    opts.Services.AddDbContextWithWolverineIntegration<SampleDbContext>(x =>
    {
        x.UseSqlServer(connectionString);
    });

    // Add the auto transaction middleware attachment policy
    opts.Policies.AutoApplyTransactions();
});

using var host = builder.Build();
await host.StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/EfCoreTests/SampleUsageWithAutoApplyTransactions.cs#L16-L34' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_bootstrapping_with_auto_apply_transactions_for_sql_server' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

With this option, you will no longer need to decorate handler methods with the `[Transactional]` attribute.

## Transaction Middleware Mode

By default, the EF Core transactional middleware uses `TransactionMiddlewareMode.Eager`, which eagerly opens an
explicit database transaction via `Database.BeginTransactionAsync()` before the handler executes. This is appropriate
when you need explicit transaction control, such as when using EF Core bulk operations.

If you prefer to rely solely on `DbContext.SaveChangesAsync()` as your transactional boundary without opening an
explicit database transaction, you can use `TransactionMiddlewareMode.Lightweight`:

```cs
builder.Host.UseWolverine(opts =>
{
    opts.PersistMessagesWithSqlServer(connectionString, "wolverine");

    // Use Lightweight mode — no explicit transaction, relies on SaveChangesAsync()
    opts.UseEntityFrameworkCoreTransactions(TransactionMiddlewareMode.Lightweight);

    opts.Policies.UseDurableLocalQueues();
});
```

::: tip
`TransactionMiddlewareMode.Lightweight` is **not** supported or necessary for Marten or RavenDb, which have their own
unit of work implementations.
:::

### Per-Handler Override

You can override the global `TransactionMiddlewareMode` for individual handlers using the `[Transactional]` attribute's
`Mode` property:

```cs
// This handler will use an explicit transaction even if the global mode is Lightweight
[Transactional(Mode = TransactionMiddlewareMode.Eager)]
public static ItemCreated Handle(CreateItemCommand command, ItemsDbContext db)
{
    var item = new Item { Name = command.Name };
    db.Items.Add(item);
    return new ItemCreated { Id = item.Id };
}

// This handler skips the explicit transaction even if the global mode is Eager
[Transactional(Mode = TransactionMiddlewareMode.Lightweight)]
public static void Handle(UpdateItemCommand command, ItemsDbContext db)
{
    // Just uses SaveChangesAsync() without an explicit transaction
}
```


## DbContext Abstractions <Badge type="tip" text="6.2" />

Sometimes the application code wants to depend on an interface that's implemented by a `DbContext`
rather than on the concrete `DbContext` itself — a `DbContext` that doubles as a custom
`IRepository`, an `IUnitOfWork`, or a similar abstraction. Wolverine's EF Core transactional
middleware can be taught to recognise those abstractions at handler-graph compile time so the
auto-applied transaction/outbox still wraps the handler. Register the abstraction with
`WithDbContextAbstraction<TAbstraction, TDbContext>()`:

<!-- snippet: sample_register_dbcontext_abstraction -->
<a id='snippet-sample_register_dbcontext_abstraction'></a>
```cs
opts.Services.AddDbContextWithWolverineIntegration<OrdersDbContext>(x =>
    x.UseNpgsql(connectionString));

// Forward the abstraction to the SAME scoped DbContext via a factory. This keeps
// `IOrderRepository` and `OrdersDbContext` pointing at one instance per scope, which is
// what `AddScoped<TAbs, TImpl>()` does NOT do (it would create a separate one per
// registered interface).
opts.Services.AddScoped<IOrderRepository>(sp => sp.GetRequiredService<OrdersDbContext>());

opts.PersistMessagesWithPostgresql(connectionString, "wolverine");

opts.UseEntityFrameworkCoreTransactions()
    .WithDbContextAbstraction<IOrderRepository, OrdersDbContext>();

opts.Policies.AutoApplyTransactions();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/EfCoreTests/dbContext_abstraction_scenarios.cs#L432-L450' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_register_dbcontext_abstraction' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

::: tip
The generic constraint `where TDbContext : DbContext, TAbstraction` means the registration only
covers abstractions that the `DbContext` implements **directly**. Wrappers around a `DbContext`
are out of scope; declare the abstraction on the `DbContext` itself.
:::

Handlers depend on the abstraction the same way they'd depend on any other service. Wolverine
emits a runtime cast at the top of the handler chain so `SaveChangesAsync` and the EF Core
outbox enrolment fire against the concrete `DbContext` underneath:

<!-- snippet: sample_handler_using_dbcontext_abstraction -->
<a id='snippet-sample_handler_using_dbcontext_abstraction'></a>
```cs
public class PlaceOrderViaAbstractionHandler
{
    public static void Handle(PlaceOrderViaAbstraction cmd, IOrderRepository orders)
    {
        // The handler depends on the abstraction. Wolverine's transactional middleware
        // recognises the chain as `DbContext`-backed via the registered abstraction and emits
        // a runtime cast at the top of the chain so SaveChangesAsync + outbox enrolment fire
        // against the concrete OrdersDbContext underneath.
        orders.Orders.Add(new OrderEntity { Id = cmd.Id, Description = cmd.Description });
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/EfCoreTests/dbContext_abstraction_scenarios.cs#L351-L365' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_handler_using_dbcontext_abstraction' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

### Multiple abstractions for the same DbContext

A single `DbContext` can implement several abstractions, and a handler may depend on more than
one of them. The contract Wolverine honours is: **both parameters resolve to the same scoped
`DbContext` instance, just viewed through different interfaces**, so a single `SaveChangesAsync`
commits all the writes the handler made through either parameter.

To make this work the abstractions must forward to the same scoped `DbContext` in DI — use a
factory registration, **not** `AddScoped<TAbstraction, TDbContext>()` (the latter would create a
separate `DbContext` per registered abstraction):

<!-- snippet: sample_register_multiple_dbcontext_abstractions -->
<a id='snippet-sample_register_multiple_dbcontext_abstractions'></a>
```cs
opts.Services.AddDbContextWithWolverineIntegration<StoreDbContext>(x =>
    x.UseNpgsql(Servers.PostgresConnectionString,
        b => b.MigrationsHistoryTable("__EFMigrationsHistory", "store_abs_schema")));

// Two abstractions forwarded to the SAME scoped DbContext instance via factory
// lambdas. `AddScoped<TAbs, TImpl>()` would create *separate* instances per
// registration; the factory form is the one users want when an abstraction is
// just a view over a DbContext that's already in the scope.
opts.Services.AddScoped<IItemRepository>(sp => sp.GetRequiredService<StoreDbContext>());
opts.Services.AddScoped<IOrderInsightRepository>(sp => sp.GetRequiredService<StoreDbContext>());

opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "wolverine_abs");

opts.UseEntityFrameworkCoreTransactions()
    .WithDbContextAbstraction<IItemRepository, StoreDbContext>()
    .WithDbContextAbstraction<IOrderInsightRepository, StoreDbContext>();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/EfCoreTests/dbContext_abstraction_scenarios.cs#L130-L149' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_register_multiple_dbcontext_abstractions' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

A handler can take both abstractions; the casts inside the chain land on the single shared
`DbContext` and one transaction commits everything atomically:

<!-- snippet: sample_handler_using_multiple_abstractions -->
<a id='snippet-sample_handler_using_multiple_abstractions'></a>
```cs
public class CrossAbstractionAuditHandler
{
    public static (bool SameInstance, Type ItemsType, Type OrdersType) LastSeen;

    // The handler depends on TWO abstractions of the same `DbContext`. At runtime both
    // parameters resolve to the same scoped `StoreDbContext`, just viewed through different
    // interfaces — so a single `SaveChangesAsync` commits writes the handler made through
    // either parameter atomically. The forwarding-factory DI registrations above are what
    // make this work; without them you'd get two separate `DbContext` instances.
    public static void Handle(CrossAbstractionAudit cmd, IItemRepository items, IOrderInsightRepository orders)
    {
        // Cast both back to the concrete DbContext - the cast must succeed (the constraint on
        // WithDbContextAbstraction guarantees TDbContext : TAbstraction) and the resulting
        // references must be the SAME instance. That's the contract Wolverine's
        // CastDbContextFrame + the user's forwarding-factory DI registrations together provide:
        // one DbContext in scope, viewed through different interfaces.
        var itemsCtx = (StoreDbContext)items;
        var ordersCtx = (StoreDbContext)orders;

        LastSeen = (ReferenceEquals(itemsCtx, ordersCtx), itemsCtx.GetType(), ordersCtx.GetType());

        // Both writes go through the single scoped DbContext - the EF Core middleware's
        // SaveChangesAsync postprocessor commits them as one transaction.
        items.Items.Add(new StoreItem { Id = cmd.ItemId, Name = "cross-abs" });
        orders.StoreOrders.Add(new StoreOrder { Id = cmd.OrderId, Status = "audited" });
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/EfCoreTests/dbContext_abstraction_scenarios.cs#L391-L421' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_handler_using_multiple_abstractions' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

### Multi-DbContext, mixed abstraction

Each `DbContext` is independent — a host can mix abstracted and non-abstracted `DbContext`s
freely. The middleware picks the right one for each handler based on its actual parameter
dependencies:

<!-- snippet: sample_register_mixed_dbcontexts -->
<a id='snippet-sample_register_mixed_dbcontexts'></a>
```cs
// First DbContext: abstracted via IOrderRepository.
opts.Services.AddDbContextWithWolverineIntegration<OrdersDbContext>(x =>
    x.UseNpgsql(Servers.PostgresConnectionString,
        b => b.MigrationsHistoryTable("__EFMigrationsHistory", "orders_abs_schema")));
opts.Services.AddScoped<IOrderRepository>(sp => sp.GetRequiredService<OrdersDbContext>());

// Second DbContext: used directly, no abstraction.
opts.Services.AddDbContextWithWolverineIntegration<CustomersDbContext>(x =>
    x.UseNpgsql(Servers.PostgresConnectionString,
        b => b.MigrationsHistoryTable("__EFMigrationsHistory", "customers_abs_schema")));

opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "wolverine_abs");

// Only OrdersDbContext is registered as having an abstraction — Wolverine's
// transactional middleware still wraps handlers that depend on
// CustomersDbContext directly.
opts.UseEntityFrameworkCoreTransactions()
    .WithDbContextAbstraction<IOrderRepository, OrdersDbContext>();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/EfCoreTests/dbContext_abstraction_scenarios.cs#L62-L83' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_register_mixed_dbcontexts' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Selecting the Transactional DbContext <Badge type="tip" text="6.17" />

A handler chain can only have **one** transactional `DbContext` — the one Wolverine enrolls in the
transaction and uses for the outbox. But a handler is often legitimately given more than one
`DbContext`-shaped dependency: one it writes through, plus one it only *reads* from — a shared
read-only lookup database, or another module's context in a modular monolith.

When a chain depends on more than one `DbContext`-shaped service, Wolverine resolves which one is
transactional in this order:

1. A `[Transactional(typeof(TDbContext))]` attribute on the handler (most specific).
2. A registration-side selection made with `WithTransactionalDbContext<T>()`.
3. **Automatic**: the single [Wolverine-enabled](#automatic-selection) candidate, if there is exactly one.

If none of these resolves to a single context, Wolverine fails fast at startup and names the
candidates.

### Automatic selection

Only a `DbContext` that mapped Wolverine's envelope storage (via `MapWolverineEnvelopeStorage`,
which `AddDbContextWithWolverineIntegration<T>()` sets up) can own the transaction and outbox. A
plain `AddDbContext<T>()` read-only lookup context therefore is **never** a transactional candidate.
When exactly one of a handler's `DbContext` dependencies is Wolverine-enabled, Wolverine selects it
automatically — no attribute, no configuration, and the read context is simply an ordinary injected
dependency:

```cs
builder.Host.UseWolverine(opts =>
{
    // The write side — enrolled in the transaction + outbox.
    opts.Services.AddDbContextWithWolverineIntegration<LedgerDbContext>(x =>
        x.UseNpgsql(connectionString));

    // A shared read-only lookup context: a plain AddDbContext<>, never enrolled.
    opts.Services.AddDbContext<RatesDbContext>(x => x.UseNpgsql(connectionString));

    opts.PersistMessagesWithPostgresql(connectionString, "wolverine");
    opts.UseEntityFrameworkCoreTransactions();
    opts.Policies.AutoApplyTransactions();
});

public class PostLedgerEntryHandler
{
    // No [Transactional] attribute and no need to name a DbContext: LedgerDbContext is the only
    // Wolverine-enabled dependency, so it is selected automatically. RatesDbContext is read-only.
    public static void Handle(PostLedgerEntry cmd, LedgerDbContext ledger, RatesDbContext rates)
    {
        ledger.Entries.Add(new LedgerEntry { Id = cmd.Id, Memo = cmd.Memo });
    }
}
```

### Explicit selection at registration

When **both** contexts are Wolverine-enabled — for example two modules that each own an outbox —
nothing can be inferred automatically. Rather than putting a `[Transactional(typeof(...))]` attribute
on the handler (which a Clean Architecture application layer should not carry), select the write
context in the composition root with `WithTransactionalDbContext<T>()`. The handler stays free of any
`DbContext` reference and any Wolverine attribute:

```cs
opts.Services.AddDbContextWithWolverineIntegration<SalesDbContext>(x => x.UseNpgsql(connectionString));
opts.Services.AddDbContextWithWolverineIntegration<AuditDbContext>(x => x.UseNpgsql(connectionString));

opts.PersistMessagesWithPostgresql(connectionString, "wolverine");

opts.UseEntityFrameworkCoreTransactions()
    // SalesDbContext is the transactional one wherever it is a handler dependency.
    .WithTransactionalDbContext<SalesDbContext>();

opts.Policies.AutoApplyTransactions();
```

The type argument can be a concrete `DbContext` **or** an abstraction registered with
`WithDbContextAbstraction<TAbstraction, TDbContext>()`, so an Application-layer handler can depend on,
and the composition root can select, an abstraction — never the concrete EF Core type:

```cs
opts.UseEntityFrameworkCoreTransactions()
    .WithDbContextAbstraction<ISalesStore, SalesDbContext>()
    .WithTransactionalDbContext<ISalesStore>();
```

### Modular monolith

In a modular monolith each module typically owns its own write `DbContext`, while a handler may read
from another module's context. Give each module's registration its own selection scoped to that
module's handler assembly, so a handler enrolls only its module's context even when it reads from
another's:

```cs
opts.UseEntityFrameworkCoreTransactions()
    .WithTransactionalDbContext<SalesDbContext>(typeof(SalesModuleHandler).Assembly)
    .WithTransactionalDbContext<AuditDbContext>(typeof(AuditModuleHandler).Assembly);
```

For finer control, an overload accepts a predicate over the handler chain
(`Func<IChain, bool>`). A selection that matches a chain but names a `DbContext` the chain does not
depend on — and two selections that conflict on the same chain — both fail fast at startup.
