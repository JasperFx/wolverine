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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/MartenTests/Distribution/Support/SingleTenantContext.cs#L67-L87' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_opt_into_wolverine_managed_subscription_distribution' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

::: tip
This option replaces the Marten `AddAsyncMarten(HotCold)` option and should not be used in combination
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