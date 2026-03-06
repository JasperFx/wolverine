# Projection/Subscription Distribution

When Wolverine is combined with Polecat and you're using
asynchronous projections or any event subscriptions with Polecat, you can achieve potentially greater
scalability for your system by letting Wolverine distribute the load evenly across a running cluster:

```cs
opts.Services.AddPolecat(m =>
    {
        m.Connection(connectionString);

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

::: tip
This option replaces the Polecat `AddAsyncDaemon(HotCold)` option and should not be used in combination
with Polecat's own load distribution.
:::

With this option, Wolverine is going to ensure that every single known asynchronous event projection and every event
subscription is running on exactly one running node within your application cluster. Moreover, Wolverine will purposely stop and
restart projections or subscriptions to spread the running load across your entire cluster of running nodes.

If a node is taken offline, Wolverine will detect that the node is no longer accessible and try to start the missing
projection/subscription agents on another active node.

_If you run your application on only a single server, Wolverine will of course run all projections and subscriptions
on just that one server._

Some other facts about this integration:

* Wolverine's agent distribution works with per-tenant database multi-tenancy
* Wolverine does automatic health checking at the running node level
* Wolverine can detect when new nodes come online and redistribute work
* Wolverine is able to support blue/green deployment
* This capability depends on Wolverine's built-in leadership election

## Uri Structure

The `Uri` structure for event subscriptions or projections is:

```
event-subscriptions://[event store type]/[event store name]/[database server].[database name]/[relative path of the shard]
```

For example: `event-subscriptions://polecat/main/localhost.mydb/day/all`

## Requirements

This functionality requires Wolverine to both track running nodes and to send messages between running nodes within your
clustered Wolverine service. Wolverine will utilize a "database control queue" for this internal messaging if you are using the `AddPolecat().IntegrateWithWolverine()` integration.

Other requirements:

* You cannot disable external transports with `StubAllExternalTransports()`
* `WolverineOptions.Durability.Mode` must be `Balanced`
