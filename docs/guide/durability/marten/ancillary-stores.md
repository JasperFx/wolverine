# "Separate" or Ancillary Stores <Badge type="tip" text="2.13" />

Let's say that you want to use the full "Critter Stack" inside of a [modular monolith architecture](https://jeremydmiller.com/2024/04/01/thoughts-on-modular-monoliths/).
With Marten, you might well want to use its ["Separate Store"](https://martendb.io/configuration/hostbuilder.html#working-with-multiple-marten-databases) feature ("ancillary" in Wolverine parlance) 
to split up the modules so they are accessing different, logical databases 
-- even if in the end everything is stored in the exact same PostgreSQL database. However, 
even with separate Marten document stores, you still want Wolverine's:

* Transaction middleware support, including the transactional outbox
* Scheduled message support -- which is really part of the outbox anyway
* Subscriptions to Marten events captured by these separate stores
* Marten side effect model (`MartenOps`)
* Ability to automatically set up the necessary envelope storage tables and functions in each database or separate schema

Well now you can get that, but there's a few explicit steps to take.

First off, you need to explicitly and individually tag each Marten store that you want to be integrated with Wolverine
in your bootstrapping.

From the Wolverine tests, say you have these two separate stores:

<!-- snippet: sample_separate_marten_stores -->
<a id='snippet-sample_separate_marten_stores'></a>
```cs
public interface IPlayerStore : IDocumentStore;

public interface IThingStore : IDocumentStore;
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/MartenTests/AncillaryStores/bootstrapping_ancillary_marten_stores_with_wolverine.cs#L254-L260' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_separate_marten_stores' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

We can add Wolverine integration to both through a similar call to `IntegrateWithWolverine()` as normal as shown below:

<!-- snippet: sample_bootstrapping_with_ancillary_marten_stores -->
<a id='snippet-sample_bootstrapping_with_ancillary_marten_stores'></a>
```cs
theHost = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {

        // THIS IS IMPORTANT FOR MODULAR MONOLITH USAGE!
        opts.Durability.MessageStorageSchemaName = "wolverine";

        opts.Services.AddMarten(Servers.PostgresConnectionString).IntegrateWithWolverine();

        opts.Policies.AutoApplyTransactions();
        opts.Durability.Mode = DurabilityMode.Solo;

        opts.Services.AddMartenStore<IPlayerStore>(m =>
            {
                m.Connection(Servers.PostgresConnectionString);
                m.DatabaseSchemaName = "players";
            })
            .IntegrateWithWolverine()

            // Add a subscription
            .SubscribeToEvents(new ColorsSubscription())

            // Forward events to wolverine handlers
            .PublishEventsToWolverine("PlayerEvents", x => { x.PublishEvent<ColorsUpdated>(); });

        // Look at that, it even works with Marten multi-tenancy through separate databases!
        opts.Services.AddMartenStore<IThingStore>(m =>
        {
            m.MultiTenantedDatabases(tenancy =>
            {
                tenancy.AddSingleTenantDatabase(tenant1ConnectionString, "tenant1");
                tenancy.AddSingleTenantDatabase(tenant2ConnectionString, "tenant2");
                tenancy.AddSingleTenantDatabase(tenant3ConnectionString, "tenant3");
            });
            m.DatabaseSchemaName = "things";
        }).IntegrateWithWolverine(masterDatabaseConnectionString: Servers.PostgresConnectionString);

        opts.Services.AddResourceSetupOnStartup();
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/MartenTests/AncillaryStores/bootstrapping_ancillary_marten_stores_with_wolverine.cs#L54-L99' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_bootstrapping_with_ancillary_marten_stores' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Let's specifically zoom in on this code from within the big sample above:

<!-- snippet: sample_using_message_storage_schema_name -->
<a id='snippet-sample_using_message_storage_schema_name'></a>
```cs
// THIS IS IMPORTANT FOR MODULAR MONOLITH USAGE!
opts.Durability.MessageStorageSchemaName = "wolverine";
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/MartenTests/AncillaryStores/bootstrapping_ancillary_marten_stores_with_wolverine.cs#L59-L64' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_message_storage_schema_name' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

If you are using separate Marten document stores for different modules in your application, you can easily make Wolverine 
happily share the transactional inbox/outbox between modules (you do *want* to do this to save on resource usage) by ensuring
that all the document stores have the same database schema for envelope storage. The `opts.Durability.MessageStorageSchemaName` 
value can be used to help Wolverine out to share the transactional inbox/outbox storage across all Marten stores that
target the same physical database.

Now, moving to message handlers or HTTP endpoints, you will have to explicitly tag either the containing class or
individual messages with the `[MartenStore(store type)]` attribute like this simple example below:

<!-- snippet: sample_PlayerMessageHandler -->
<a id='snippet-sample_playermessagehandler'></a>
```cs
// This will use a Marten session from the
// IPlayerStore rather than the main IDocumentStore
[MartenStore(typeof(IPlayerStore))]
public static class PlayerMessageHandler
{
    // Using a Marten side effect just like normal
    public static IMartenOp Handle(PlayerMessage message)
    {
        return MartenOps.Store(new Player { Id = message.Id });
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/MartenTests/AncillaryStores/bootstrapping_ancillary_marten_stores_with_wolverine.cs#L238-L252' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_playermessagehandler' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

::: info
At this point the "Critter Stack" team is voting to make the attribute an explicit requirement rather than trying
any kind of conventional application of what handlers/messages/HTTP routes are covered by what Marten document store
:::

So what's possible so far?

* The transactional inbox support is available in all configured Marten stores
* Transactional middleware
* The "aggregate handler workflow"
* Marten side effects
* Subscriptions to Marten events
* Multi-tenancy, both "conjoined" Marten multi-tenancy and multi-tenancy through separate databases

::: tip
In the case of the ancillary Marten stores, the `IDocumentSession` objects are "lightweight" sessions without
any identity map mechanics for better performance. 
:::

## What's not (yet) supported

::: warning
There is currently a limitation where Wolverine can only use the inbox/outbox storage from the main Marten
document store even if your handler declares that it is from a separate store. This is perfectly fine if your
ancillary stores target the same PostgreSQL database. This limitation will be removed in 3.0.
:::

* It is not possible to use more than one ancillary store in the same handler with the middleware
* The "Event Forwarding" from Marten to Wolverine
* Fine grained configuration of the `IDocumentSession` objects created for the ancillary stores, so no ability to tag
  custom `IDocumentSessionListener` objects or control the session type. Listeners could be added through Wolverine middlware
  though
* Controlling which schema the Wolverine envelope tables are placed in. Today they will be placed in the default schema for
  the ancillary store
* The PostgreSQL messaging transport will not span the ancillary databases, but will still work if the ancillary store is targeting
  the same database



