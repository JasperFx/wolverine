# "Separate" or Ancillary Polecat Stores

Just like the [ancillary Marten stores](/guide/durability/marten/ancillary-stores) feature, Wolverine can integrate
with secondary ("ancillary" in Wolverine parlance) Polecat document stores for [modular monolith
architectures](https://jeremydmiller.com/2024/04/01/thoughts-on-modular-monoliths/). Each module can use its own
logical Polecat `IDocumentStore` -- even when everything ultimately lives in the same SQL Server database -- while
still getting Wolverine's:

* Transaction middleware support, including the transactional outbox
* Scheduled message support
* The Polecat side effect model (`IPolecatOp`)
* Inline-projection side effects relayed through the Wolverine outbox
* Automatic setup of the necessary envelope storage tables in each database or schema

## Registering the stores

A secondary store is identified by a marker interface that inherits from Polecat's `IDocumentStore`:

```cs
public interface IPlayerStore : IDocumentStore;

public interface IThingStore : IDocumentStore;
```

Register each ancillary store with `AddPolecatStore<T>()` and opt into Wolverine with `IntegrateWithWolverine()`,
exactly as you would for the primary store:

```cs
theHost = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // IMPORTANT FOR MODULAR MONOLITH USAGE: share one envelope storage schema
        // across all modules for more efficient use of resources.
        opts.Durability.MessageStorageSchemaName = "wolverine";
        opts.Policies.AutoApplyTransactions();

        // Primary Polecat store
        opts.Services.AddPolecat(m =>
            {
                m.ConnectionString = connectionString;
                m.DatabaseSchemaName = "main";
            })
            .UseLightweightSessions()
            .IntegrateWithWolverine();

        // Ancillary Polecat store on the same SQL Server, different document schema
        opts.Services.AddPolecatStore<IPlayerStore>(m =>
            {
                m.Connection(connectionString);
                m.DatabaseSchemaName = "players";
            })
            .IntegrateWithWolverine();

        opts.Services.AddResourceSetupOnStartup();
    }).StartAsync();
```

As with Marten, setting `opts.Durability.MessageStorageSchemaName` lets Wolverine share one transactional inbox/outbox
across every Polecat store that targets the same physical database.

## Routing a handler to an ancillary store

Tag the handler class (or an individual handler method) with `[PolecatStore(store type)]` so its session is opened from
that store rather than the primary `IDocumentStore`:

```cs
// This uses a Polecat session from the IPlayerStore ancillary store
// rather than the main IDocumentStore.
[PolecatStore(typeof(IPlayerStore))]
public static class PlayerMessageHandler
{
    public static void Handle(PlayerMessage message, IDocumentSession session)
    {
        session.Store(new Player { Id = message.Id });
    }
}
```

## Provider-agnostic `[Storage]` attribute <Badge type="tip" text="6.9" />

If you don't want your handler code to be coupled to a specific persistence tool, use the provider-agnostic
`[Storage(store type)]` attribute from the `Wolverine.Persistence` namespace instead of `[PolecatStore]` (or
`[MartenStore]`). Wolverine resolves the owning integration -- Polecat, Marten, ... -- from the store marker type, so
the very same attribute works regardless of which Critter Stack tool backs that store:

```cs
using Wolverine.Persistence;

[Storage(typeof(IPlayerStore))]
public static class PlayerMessageHandler
{
    public static void Handle(PlayerMessage message, IDocumentSession session)
    {
        session.Store(new Player { Id = message.Id });
    }
}
```

This is handy when a set of handlers is shared across a Marten flavor and a Polecat/SQL-Server flavor of an
application -- one attribute, both stores. If you want to route an entire assembly of handlers to one ancillary store
without per-handler attributes, call `chain.UsePolecatStore(storeType)` (or the provider-agnostic
`chain.UseAncillaryStorage(storeType, container)`) from an `IChainPolicy`.

## Multi-tenancy through separate databases

Polecat multi-tenancy is *database per tenant*. An ancillary store can be multi-tenanted, and Wolverine will build a
`MultiTenantedMessageStore` for it:

```cs
opts.Services.AddPolecatStore<IThingStore>(m =>
    {
        // Polecat still needs a base connection string (the default-tenant / master database)
        m.ConnectionString = masterConnectionString;
        m.MultiTenantedDatabases(tenancy =>
        {
            tenancy.AddTenant("tenant1", tenant1ConnectionString);
            tenancy.AddTenant("tenant2", tenant2ConnectionString);
        });
        m.DatabaseSchemaName = "things";
    })
    .IntegrateWithWolverine(x => x.MainConnectionString = masterConnectionString);
```

::: info
Unlike Marten, Polecat does not support "conjoined" tenant-partitioned events
(`UseTenantPartitionedEvents`). Multi-tenant ancillary Polecat stores use a separate database per tenant.
:::

## Inline-projection side effects

A projection registered against an ancillary store can publish Wolverine messages from a `RaiseSideEffects` override,
and those messages relay through the Wolverine outbox after the projection batch commits. Opt in per store with
`Events.EnableSideEffectsOnInlineProjections`:

```cs
opts.Services.AddPolecatStore<ISideEffectStore>(m =>
    {
        m.Connection(connectionString);
        m.DatabaseSchemaName = "se_ancillary";
        m.Events.EnableSideEffectsOnInlineProjections = true;
        m.Projections.Add(new CounterSideEffectProjection(), ProjectionLifecycle.Inline);
    })
    .IntegrateWithWolverine();
```

::: tip
As with the ancillary Marten stores, the `IDocumentSession` objects created for ancillary Polecat stores are
"lightweight" sessions without identity-map mechanics for better performance.
:::

## What's not (yet) supported

* It is not possible to use more than one ancillary store in the same handler with the middleware
* Conjoined / tenant-partitioned events (Polecat multi-tenancy is database-per-tenant)
* The SQL Server messaging transport will not span the ancillary databases, but still works when the ancillary store
  targets the same database as the primary store
