# Event Subscriptions

::: tip
The older [Event Forwarding](./event-forwarding) feature is a subset of subscriptions that relies on the Polecat transactional middleware in message handlers or HTTP endpoints, but happens at the time of event
capture whereas the event subscriptions are processed in strict order in a background process through Polecat's async daemon
subsystem **and do not require you to use the Polecat transactional middleware for every operation**. The **strong suggestion from the Wolverine team is to use one or the other approach, but not both in the same system**.
:::

Wolverine has the ability to extend Polecat's event subscription functionality to carry out message processing by Wolverine on
the events being captured by Polecat in strict order. This functionality works through Polecat's async daemon.

There are easy recipes for processing events through Wolverine message handlers, and also for just publishing events
through Wolverine's normal message publishing to be processed locally or by being propagated through asynchronous messaging
to other systems.

::: info
Note that Polecat itself will guarantee that each subscription is only running on one active node at a time.
:::

## Publish Events as Messages

::: tip
Unless you really want to publish every single event captured by Polecat, set up event type filters to make the subscription
do less work at runtime.
:::

The simplest recipe is to just ask Polecat to publish events -- in strict order -- to Wolverine subscribers:

```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.Services
            .AddPolecat(o =>
            {
                o.Connection(connectionString);
            })
            .IntegrateWithWolverine()
            .AddAsyncDaemon(DaemonMode.HotCold)

            // This would attempt to publish every non-archived event
            // from Polecat to Wolverine subscribers
            .PublishEventsToWolverine("Everything")

            // Or with filtering
            .PublishEventsToWolverine("Orders", relay =>
            {
                relay.FilterIncomingEventsOnStreamType(typeof(Order));
                relay.Options.SubscribeFromPresent();
            });
    }).StartAsync();
```

First off, what's a "subscriber?" *That* would mean any event that Wolverine recognizes as having:

* A local message handler in the application for the specific event type
* A local message handler in the application for the specific `IEvent<T>` type
* Any event type where Wolverine can discover subscribers through routing rules

## Process Events as Messages in Strict Order

In some cases you may want the events to be executed by Wolverine message handlers in strict order:

```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.Services
            .AddPolecat(o =>
            {
                o.Connection(connectionString);
                o.Projections.Errors.SkipApplyErrors = true;
            })
            .IntegrateWithWolverine()
            .AddAsyncDaemon(DaemonMode.HotCold)
            .ProcessEventsWithWolverineHandlersInStrictOrder("Orders", o =>
            {
                o.IncludeType<OrderCreated>();
                o.Options.SubscribeFromTime(new DateTimeOffset(new DateTime(2023, 12, 1)));
            });
    }).StartAsync();
```

In this recipe, Polecat & Wolverine are working together to call `IMessageBus.InvokeAsync()` on each event in order.

In the case of exceptions from processing the event with Wolverine:

1. Any built in "retry" error handling will kick in to retry the event processing inline
2. If the retries are exhausted, and `SkipApplyErrors = true`, Wolverine will persist the event to its SQL Server backed dead letter queue and proceed to the next event
3. If the retries are exhausted, and `SkipApplyErrors = false`, Wolverine will direct Polecat to pause the subscription

## Custom Subscriptions

The base type for all Wolverine subscriptions is the `Wolverine.Polecat.Subscriptions.BatchSubscription` class. If you need
to do something completely custom, or just to take action on a batch of events at one time, subclass that type:

```cs
public record CompanyActivated(string Name);
public record CompanyDeactivated;
public record NewCompany(Guid Id, string Name);

public class CompanyActivations
{
    public List<NewCompany> Additions { get; set; } = new();
    public List<Guid> Removals { get; set; } = new();
}

public class CompanyTransferSubscription : BatchSubscription
{
    public CompanyTransferSubscription() : base("CompanyTransfer")
    {
        IncludeType<CompanyActivated>();
        IncludeType<CompanyDeactivated>();
    }

    public override async Task ProcessEventsAsync(EventRange page, ISubscriptionController controller,
        IDocumentOperations operations,
        IMessageBus bus, CancellationToken cancellationToken)
    {
        var activations = new CompanyActivations();
        foreach (var e in page.Events)
        {
            switch (e)
            {
                case IEvent<CompanyActivated> activated:
                    activations.Additions.Add(new NewCompany(activated.StreamId, activated.Data.Name));
                    break;
                case IEvent<CompanyDeactivated> deactivated:
                    activations.Removals.Add(deactivated.StreamId);
                    break;
            }
        }

        await bus.PublishAsync(activations);
    }
}
```

And the related code to register this subscription:

```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UseRabbitMq();

        opts.PublishMessage<CompanyActivations>()
            .ToRabbitExchange("activations");

        opts.Services
            .AddPolecat(o =>
            {
                o.Connection(connectionString);
            })
            .IntegrateWithWolverine()
            .AddAsyncDaemon(DaemonMode.HotCold)
            .SubscribeToEvents(new CompanyTransferSubscription());
    }).StartAsync();
```

## Using IoC Services in Subscriptions

To use IoC services in your subscription, use constructor injection and the `SubscribeToEventsWithServices<T>()` API:

```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.Services
            .AddPolecat(o =>
            {
                o.Connection(connectionString);
            })
            .IntegrateWithWolverine()
            .AddAsyncDaemon(DaemonMode.HotCold)
            .SubscribeToEventsWithServices<CompanyTransferSubscription>(ServiceLifetime.Scoped);
    }).StartAsync();
```
