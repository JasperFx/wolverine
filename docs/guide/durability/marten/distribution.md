# Projection/Subscription Distribution <Badge type="tip" text="3.0" />

When Wolverine is combined with Marten into the full "Critter Stack" combination, and you're using
the asynchronous projection or any event subscriptions with Marten, you can achieve potentially greater
scalability for your system by better distributing the background work of these asynchronous event workers
by letting Wolverine distribute the load evenly across a running cluster as shown below:

<!-- snippet: sample_opt_into_wolverine_managed_subscription_distribution -->
<a id='snippet-sample_opt_into_wolverine_managed_subscription_distribution'></a>
```cs
opts.Services.AddMarten(m =>
    {
        m.DisableNpgsqlLogging = true;
        m.Connection(Servers.PostgresConnectionString);
        m.DatabaseSchemaName = "csp";

        m.Projections.Add<TripProjection>(ProjectionLifecycle.Async);
        m.Projections.Add<DayProjection>(ProjectionLifecycle.Async);
        m.Projections.Add<DistanceProjection>(ProjectionLifecycle.Async);
    })
    .IntegrateWithWolverine(m =>
    {
        // This makes Wolverine distribute the registered projections
        // and event subscriptions evenly across a running application
        // cluster
        m.UseWolverineManagedEventSubscriptionDistribution = true;
    });
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/MartenTests/Distribution/Support/SingleTenantContext.cs#L71-L91' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_opt_into_wolverine_managed_subscription_distribution' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

::: tip
This option replaces the Marten `AddAsyncDaemon(HotCold)` option and should not be used in combination
with Marten's own load distribution.
:::

With this option, Wolverine is going to ensure that every single known asynchronous [event projection](https://martendb.io/events/projections/) and every [event
subscription](https://martendb.io/events/subscriptions.html) is running on exactly one running node within your application cluster. Moreover, Wolverine will purposely stop and
restart projections or subscriptions to purposely spread the running load across your entire cluster of running nodes.

In the case of using multi-tenancy through separate databases per tenant with Marten, this Wolverine "agent distribution"
will assign the work by tenant databases, meaning that all the running projections and subscriptions for a single tenant
database will always be running on a single application node. This was done with the theory that this affinity would hopefully
reduce the number of used database connections over all.

If a node is taken offline, Wolverine will detect that the node is no longer accessible and try to move start the missing
projection/subscription agents on another active node. 

_If you run your application on only a single server, Wolverine will of course run all projections and subscriptions
on just that one server._

Some other facts about this integration:

* Wolverine's agent distribution does indeed work with per-tenant database multi-tenancy
* Wolverine does automatic health checking at the running node level so that it can fail over assigned agents
* Wolverine can detect when new nodes come online and redistribute work
* Wolverine is able to support blue/green deployment and only run projections or subscriptions on active nodes
  where a capability is present. This just means that you can add all new projections or subscriptions, or even just
  new versions of a projection or subscription on some application nodes in order to do try ["blue/green deployment."](https://en.wikipedia.org/wiki/Blue%E2%80%93green_deployment)
* This capability does depend on Wolverine's built-in [leadership election](https://en.wikipedia.org/wiki/Leader_election) -- which fortunately got a _lot_ better in Wolverine 3.0

## Uri Structure

The `Uri` structure for event subscriptions or projections is:

```
event-subscriptions://[event store type]/[event store name]/[database server].[database name]/[relative path of the shard]
```

For an example from the tests: `event-subscriptions://marten/main/localhost.postgres/day/all` where:

* "marten" means that its a [Marten](https://martendb.io) based event store (we are planning on at least a SQL Server backed event store some day besides Marten)
* "main" refers to this projection being in the main `DocumentStore` Marten store that is added from `IServiceCollection.AddMarten()`. Otherwise this value would be the type name of an ancillary store
  type in all lower case
* "localhost" is the database server
* "postgres" is the name of the database
* "day/all" refers to a projection with the `ShardName` of "Day:All"

## Requirements

This functionality requires Wolverine to both track running nodes and to send messages between running nodes within your
clustered Wolverine service. One way or another, Wolverine needs some kind of "control queue" mechanism for this internal
messaging. Not to worry though, because Wolverine will utilize in a very basic "database control queue" specifically for
this if you are using the `AddMarten().IntegrateWithWolverine()` integration or any database backed message persistence as a default
if you are not using any kind of external messaging broker that supports Wolverine control queues. 

At the point of this writing, the Rabbit MQ and Azure Service Bus transport options both create a "control queue" for each
executing Wolverine node that Wolverine can use for this communication in a more efficient way than the database backed 
control queue mechanism. 

Other requirements:

* You cannot disable external transports with the `StubAllExternalTransports()`
* `WolverineOptions.Durability.Mode` must be `Balanced`

If you are seeing any issues with timeouts due to the Wolverine load distribution, you can try:

1. Pre-generating any Marten types to speed up the "cold start" time
2. Use the `WolverineOptions.Durability.Mode = Solo` setting at development time
3. Try to use an external broker for faster communication between nodes

## With Ancillary Marten Stores <Badge type="tip" text="5.0" />

Wolverine can also distribute projections and subscriptions running in [ancillary stores](/guide/durability/marten/ancillary-stores) as well. In this case,
you do have to enable the Wolverine managed distribution on the main Marten store registration, but that applies to
all known ancillary stores. 

<!-- snippet: sample_using_distributed_projections_with_ancillary_stores -->
<a id='snippet-sample_using_distributed_projections_with_ancillary_stores'></a>
```cs
var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.Durability.HealthCheckPollingTime = 1.Seconds();
        opts.Durability.CheckAssignmentPeriod = 1.Seconds();
        
        opts.UseMessagePackSerialization();
        
        opts.UseSharedMemoryQueueing();
        
        opts.Services.AddMarten(m =>
            {
                m.DisableNpgsqlLogging = true;
                m.Connection(Servers.PostgresConnectionString);
                m.DatabaseSchemaName = "csp2";

                m.Projections.Add<TripProjection>(ProjectionLifecycle.Async);
                m.Projections.Add<DayProjection>(ProjectionLifecycle.Async);
                m.Projections.Add<DistanceProjection>(ProjectionLifecycle.Async);
            })
            .IntegrateWithWolverine(m =>
            {
                // This makes Wolverine distribute the registered projections
                // and event subscriptions evenly across a running application
                // cluster
                m.UseWolverineManagedEventSubscriptionDistribution = true;
            });
        
        opts.Services.AddSingleton<ILoggerProvider>(new OutputLoggerProvider(_output));

        opts.Services.AddMartenStore<ITripStore>(m =>
        {
            m.DisableNpgsqlLogging = true;
            m.Connection(Servers.PostgresConnectionString);
            m.DatabaseSchemaName = "csp3";

            m.Projections.Add<TripProjection>(ProjectionLifecycle.Async);
            m.Projections.Add<DayProjection>(ProjectionLifecycle.Async);
            m.Projections.Add<DistanceProjection>(ProjectionLifecycle.Async);
        }).IntegrateWithWolverine();

        opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/MartenTests/Distribution/with_ancillary_stores.cs#L75-L121' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_distributed_projections_with_ancillary_stores' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

