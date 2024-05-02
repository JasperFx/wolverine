# Event Subscriptions <Badge type="tip" text="2.2" />

::: tip
The older [Event Forwarding](./event-forwarding) feature is a subset of subscriptions that relies on the Marten transactional middleware in message handlers or HTTP endpoints, but happens at the time of event
capture whereas the event subscriptions are processed in strict order in a background process through Marten's [async daemon](https://martendb.io/events/projections/async-daemon.html)
subsystem **and do not require you to use the Marten transactional middleware for every operation**. The **strong suggestion from the Wolverine team is to use one or the other approach, but not both in the same system**.
:::

Wolverine has the ability to extend Marten's [event subscription functionality](https://martendb.io/events/subscriptions.html) to carry out message processing by Wolverine on
the events being captured by Marten in strict order. This new functionality works through Marten's [async daemon](https://martendb.io/events/projections/async-daemon.html)

There are easy recipes for processing events through Wolverine message handlers, and also for just publishing events
through Wolverine's normal message publishing to be processed locally or by being propagated through asynchronous messaging
to other systems:

![Wolverine Subscription Recipes](/wolverine-subscriptions.png)

::: info
Note that in all cases Marten itself will guarantee that each subscription (for each tenant database) is only running on one active node at a time.
You may want to purposely segment subscriptions by event types to better distribute work across a running cluster of system
nodes.
:::

## Publish Events as Messages

::: tip
Unless you really want to publish every single event captured by Marten, set up event type filters to make the subscription
do less work at runtime. No sense fetching and deserializing event data from the database that you end up not using at all!
:::

The simplest recipe is to just ask Marten to publish events -- in strict order -- to Wolverine subscribers as shown with the 
usage of the `PublishEventsToWolverine()` API below that is chained after the `AddMarten()` declaration:

<!-- snippet: sample_publish_events_to_wolverine_subscribers -->
<a id='snippet-sample_publish_events_to_wolverine_subscribers'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.Services
            .AddMarten()
            
            // Just pulling the connection information from 
            // the IoC container at runtime.
            .UseNpgsqlDataSource()
            
            // You don't absolutely have to have the Wolverine
            // integration active here for subscriptions, but it's
            // more than likely that you will want this anyway
            .IntegrateWithWolverine()
            
            // The Marten async daemon most be active
            .AddAsyncDaemon(DaemonMode.HotCold)
            
            // This would attempt to publish every non-archived event
            // from Marten to Wolverine subscribers
            .PublishEventsToWolverine("Everything")
            
            // You wouldn't do this *and* the above option, but just to show
            // the filtering
            .PublishEventsToWolverine("Orders", relay =>
            {
                // Filtering 
                relay.FilterIncomingEventsOnStreamType(typeof(Order));

                // Optionally, tell Marten to only subscribe to new
                // events whenever this subscription is first activated
                relay.Options.SubscribeFromPresent();
            });
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/MartenSubscriptionSamples.cs#L21-L58' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_publish_events_to_wolverine_subscribers' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

::: tip
Be careful with this feature if you are using any kind of automatic or conventional message routing that automatically routes
messages based on the message type names or other criteria. In this case, you may want to filter the subscription to create
an allow list of 
:::

First off, what's a "subscriber?" *That* would mean any event that Wolverine recognizes as having:

* A local message handler in the application for the specific event type, which would effectively direct Wolverine to publish
  the event data to a local queue
* A local message handler in the application for the specific `IEvent<T>` type, which would effectively direct Wolverine to publish
  the event with its `IEvent` Marten metadata wrapper to a local queue
* Any event type where Wolverine can discover subscribers through routing rules

All the Wolverine subscription is doing is effectively calling `IMessageBus.PublishAsync()` against the event data or the 
`IEvent<T>` wrapper. You can make the subscription run more efficiently by applying event or stream type filters for 
the subscription. 

If you need to do a transformation of the raw `IEvent<T>` or the internal event type to some kind of external event type
for publishing to external systems when you want to avoid directly coupling other subscribers to your system's internals,
you can accomplish that by just building a message handler that does the transformation and publishes a cascading message like
so:

<!-- snippet: sample_transforming_event_to_external_integration_events -->
<a id='snippet-sample_transforming_event_to_external_integration_events'></a>
```cs
public record OrderCreated(string OrderNumber, Guid CustomerId);

// I wouldn't use this kind of suffix in real life, but it helps
// document *what* this is for the sample in the docs:)
public record OrderCreatedIntegrationEvent(string OrderNumber, string CustomerName, DateTimeOffset Timestamp);

// We're going to use the Marten IEvent metadata and some other Marten reference
// data to transform the internal OrderCreated event
// to an OrderCreatedIntegrationEvent that will be more appropriate for publishing to
// external systems
public static class InternalOrderCreatedHandler
{
    public static Task<Customer?> LoadAsync(IEvent<OrderCreated> e, IQuerySession session,
        CancellationToken cancellationToken)
        => session.LoadAsync<Customer>(e.Data.CustomerId, cancellationToken);
    
    
    public static OrderCreatedIntegrationEvent Handle(IEvent<OrderCreated> e, Customer customer)
    {
        return new OrderCreatedIntegrationEvent(e.Data.OrderNumber, customer.Name, e.Timestamp);
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/MartenSubscriptionSamples.cs#L178-L203' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_transforming_event_to_external_integration_events' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Process Events as Messages in Strict Order

In some cases you may want the events to be executed by Wolverine message handlers in strict order. With the recipe below:

<!-- snippet: sample_inline_invocation_of_wolverine_messages_for_marten_subscription -->
<a id='snippet-sample_inline_invocation_of_wolverine_messages_for_marten_subscription'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.Services
            .AddMarten(o =>
            {
                // This is the default setting, but just showing
                // you that Wolverine subscriptions will be able
                // to skip over messages that fail without
                // shutting down the subscription
                o.Projections.Errors.SkipApplyErrors = true;
            })

            // Just pulling the connection information from 
            // the IoC container at runtime.
            .UseNpgsqlDataSource()

            // You don't absolutely have to have the Wolverine
            // integration active here for subscriptions, but it's
            // more than likely that you will want this anyway
            .IntegrateWithWolverine()
            
            // The Marten async daemon most be active
            .AddAsyncDaemon(DaemonMode.HotCold)
            
            // Notice the allow list filtering of event types and the possibility of overriding
            // the starting point for this subscription at runtime
            .ProcessEventsWithWolverineHandlersInStrictOrder("Orders", o =>
            {
                // It's more important to create an allow list of event types that can be processed
                o.IncludeType<OrderCreated>();

                // Optionally mark the subscription as only starting from events from a certain time
                o.Options.SubscribeFromTime(new DateTimeOffset(new DateTime(2023, 12, 1)));
            });
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/MartenSubscriptionSamples.cs#L63-L102' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_inline_invocation_of_wolverine_messages_for_marten_subscription' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

In this recipe, Marten & Wolverine are working together to call `IMessageBus.InvokeAsync()` on each event in order. You can
use both the actual event type (`OrderCreated`) or the wrapped Marten event type (`IEvent<OrderCreated>`) as the message
type for your message handler. 

::: tip
Wolverine will log all exceptions regardless of your configuration
:::

In the case of exceptions from processing the event with Wolverine:

1. Any built in "retry" error handling will kick in to retry the event processing inline
2. If the retries are exhausted, and the Marten setting for `StoreOptions.Projections.Errors.SkipApplyErrors` is `true`,
   Wolverine will persist the event to its PostgreSQL backed dead letter queue and proceed to the next event. This setting
   is the default with Marten when the daemon is running continuously in the background, but `false` in rebuilds or replays
3. If the retries are exchausted, and `SkipApplyErrors = false`, Wolverine will still 


## Custom Subscriptions

::: info
The example below is pretty well exactly the first usage of this feature for a [JasperFx Software](https://jasperfx.net) client.
:::

The base type for all Wolverine subscriptions is the `Wolverine.Marten.Subscriptions.BatchSubscription` class. If you need
to do something completely custom, or just to take action on a batch of events at one time, subclass that type. Here is an 
example usage where I'm using [event carried state transfer](https://martinfowler.com/articles/201701-event-driven.html) to 
publish batches of reference data about customers being activated or deactivated within our system:

<!-- snippet: sample_CompanyTransferSubscriptions -->
<a id='snippet-sample_companytransfersubscriptions'></a>
```cs
public record CompanyActivated(string Name);

public record CompanyDeactivated();

public record NewCompany(Guid Id, string Name);

// Message type we're going to publish to external
// systems to keep them up to date on new companies
public class CompanyActivations
{
    public List<NewCompany> Additions { get; set; } = new();
    public List<Guid> Removals { get; set; } = new();

    public void Add(Guid companyId, string name)
    {
        Removals.Remove(companyId);
        
        // Fill is an extension method in JasperFx.Core that adds the 
        // record to a list if the value does not already exist
        Additions.Fill(new NewCompany(companyId, name));
    }

    public void Remove(Guid companyId)
    {
        Removals.Fill(companyId);

        Additions.RemoveAll(x => x.Id == companyId);
    }
}

public class CompanyTransferSubscription : BatchSubscription
{
    public CompanyTransferSubscription() : base("CompanyTransfer")
    {
        IncludeType<CompanyActivated>();
        IncludeType<CompanyDeactivated>();
    }

    public override async Task ProcessEventsAsync(EventRange page, ISubscriptionController controller, IDocumentOperations operations,
        IMessageBus bus, CancellationToken cancellationToken)
    {
        var activations = new CompanyActivations();
        foreach (var e in page.Events)
        {
            switch (e)
            {
                // In all cases, I'm assuming that the Marten stream id is the identifier for a customer
                case IEvent<CompanyActivated> activated:
                    activations.Add(activated.StreamId, activated.Data.Name);
                    break;
                case IEvent<CompanyDeactivated> deactivated:
                    activations.Remove(deactivated.StreamId);
                    break;
            }
        }
        
        // At the end of all of this, publish a single message
        // In case you're wondering, this will opt into Wolverine's
        // transactional outbox with the same transaction as any changes
        // made by Marten's IDocumentOperations passed in, including Marten's
        // own work to track the progression of this subscription
        await bus.PublishAsync(activations);
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/MartenSubscriptionSamples.cs#L206-L273' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_companytransfersubscriptions' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

And the related code to register this subscription:

<!-- snippet: sample_registering_a_batched_subscription -->
<a id='snippet-sample_registering_a_batched_subscription'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UseRabbitMq(); 
        
        // There needs to be *some* kind of subscriber for CompanyActivations
        // for this to work at all
        opts.PublishMessage<CompanyActivations>()
            .ToRabbitExchange("activations");
        
        opts.Services
            .AddMarten()

            // Just pulling the connection information from 
            // the IoC container at runtime.
            .UseNpgsqlDataSource()
            
            .IntegrateWithWolverine()
            
            // The Marten async daemon most be active
            .AddAsyncDaemon(DaemonMode.HotCold)

                                
            // Register the new subscription
            .SubscribeToEvents(new CompanyTransferSubscription());
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/MartenSubscriptionSamples.cs#L107-L136' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_registering_a_batched_subscription' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Using IoC Services in Subscriptions

To use IoC services in your subscription, you can use constructor injection within the actual subscription class
and register the projection with this slightly different usage using the `SubscribeToEventsWithServices<T>()` API:

<!-- snippet: sample_registering_a_batched_subscription_with_services -->
<a id='snippet-sample_registering_a_batched_subscription_with_services'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UseRabbitMq(); 
        
        // There needs to be *some* kind of subscriber for CompanyActivations
        // for this to work at all
        opts.PublishMessage<CompanyActivations>()
            .ToRabbitExchange("activations");

        opts.Services
            .AddMarten()

            // Just pulling the connection information from 
            // the IoC container at runtime.
            .UseNpgsqlDataSource()

            .IntegrateWithWolverine()

            // The Marten async daemon most be active
            .AddAsyncDaemon(DaemonMode.HotCold)

            // Register the new subscription
            // With this alternative you can inject services into your subscription's constructor
            // function
            .SubscribeToEventsWithServices<CompanyTransferSubscription>(ServiceLifetime.Scoped);

    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/MartenSubscriptionSamples.cs#L141-L174' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_registering_a_batched_subscription_with_services' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

See the [Marten documentation on subscriptions](/guide/durability/marten/subscriptions.html#using-ioc-services-in-subscriptions) for more information about the lifecycle and mechanics. 
