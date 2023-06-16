# Aggregate Handlers and Event Sourcing

::: tip
You can forgo the `[AggregateHandler]` attribute by instead naming your message handler type with the `AggregateHandler` suffix
if the Wolverine/Marten integration is applied to your application. Do note that you will still have to use the attribute to opt into
exclusive write locking.
:::

See the [OrderEventSourcingSample project on GitHub](https://github.com/JasperFx/wolverine/tree/main/src/Samples/OrderEventSourcingSample) for more samples.

That Wolverine + Marten combination is optimized for efficient and productive development using a [CQRS architecture style](https://martinfowler.com/bliki/CQRS.html) with [Marten's event sourcing](https://martendb.io/events/) support.
Specifically, let's dive into the responsibilities of a typical command handler in a CQRS with event sourcing architecture:

1. Fetch any current state of the system that's necessary to evaluate or validate the incoming event
2. *Decide* what events should be emitted and captured in response to an incoming event
3. Manage concurrent access to system state
4. Safely commit the new events
5. Selectively publish some of the events based on system needs to other parts of your system or even external systems
6. Instrument all of the above

And then lastly, you're going to want some resiliency and selective retry capabilities for concurrent access violations or just normal infrastructure hiccups.

Let's just right into an example order management system. I'm going to model the order workflow with this aggregate model:

<!-- snippet: sample_Order_event_sourced_aggregate -->
<a id='snippet-sample_order_event_sourced_aggregate'></a>
```cs
public class Item
{
    public string Name { get; set; }
    public bool Ready { get; set; }
}

public class Order
{
    public Order(OrderCreated created)
    {
        foreach (var item in created.Items) Items[item.Name] = item;
    }

    // This would be the stream id
    public Guid Id { get; set; }

    // This is important, by Marten convention this would
    // be the
    public int Version { get; set; }

    public DateTimeOffset? Shipped { get; private set; }

    public Dictionary<string, Item> Items { get; set; } = new();

    // These methods are used by Marten to update the aggregate
    // from the raw events
    public void Apply(IEvent<OrderShipped> shipped)
    {
        Shipped = shipped.Timestamp;
    }

    public void Apply(ItemReady ready)
    {
        Items[ready.Name].Ready = true;
    }

    public bool IsReadyToShip()
    {
        return Shipped == null && Items.Values.All(x => x.Ready);
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/OrderEventSourcingSample/Order.cs#L19-L63' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_order_event_sourced_aggregate' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

At a minimum, we're going to want a command handler for this command message that marks an order item as ready to ship and then evaluates whether
or not based on the current state of the `Order` aggregate whether or not the logical order is ready to be shipped out:

<!-- snippet: sample_MarkItemReady -->
<a id='snippet-sample_markitemready'></a>
```cs
// OrderId refers to the identity of the Order aggregate
public record MarkItemReady(Guid OrderId, string ItemName, int Version);
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/OrderEventSourcingSample/Order.cs#L65-L70' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_markitemready' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

In the code above we're also utilizing Wolverine's [outbox messaging](/guide/durability/) support to both order and guarantee the delivery of a `ShipOrder` message when
the Marten transaction

Before getting into Wolverine middleware strategies, let's first build out an MVC controller method for the command above:

<!-- snippet: sample_MarkItemController -->
<a id='snippet-sample_markitemcontroller'></a>
```cs
[HttpPost("/orders/itemready")]
public async Task Post(
    [FromBody] MarkItemReady command,
    [FromServices] IDocumentSession session,
    [FromServices] IMartenOutbox outbox
)
{
    // This is important!
    outbox.Enroll(session);
    
    // Fetch the current value of the Order aggregate
    var stream = await session
        .Events

        // We're also opting into Marten optimistic concurrency checks here
        .FetchForWriting<Order>(command.OrderId, command.Version);

    var order = stream.Aggregate;

    if (order.Items.TryGetValue(command.ItemName, out var item))
    {
        item.Ready = true;

        // Mark that the this item is ready
        stream.AppendOne(new ItemReady(command.ItemName));
    }
    else
    {
        // Some crude validation
        throw new InvalidOperationException($"Item {command.ItemName} does not exist in this order");
    }

    // If the order is ready to ship, also emit an OrderReady event
    if (order.IsReadyToShip())
    {
        // Publish a cascading command to do whatever it takes
        // to actually ship the order
        // Note that because the context here is enrolled in a Wolverine
        // outbox, the message is registered, but not "released" to
        // be sent out until SaveChangesAsync() is called down below
        await outbox.PublishAsync(new ShipOrder(command.OrderId));
        stream.AppendOne(new OrderReady());
    }

    // This will also persist and flush out any outgoing messages
    // registered into the context outbox
    await session.SaveChangesAsync();
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/OrderEventSourcingSample/Order.cs#L74-L125' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_markitemcontroller' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Hopefully that code is easy to understand, but there's some potentially repetitive code
(loading aggregates, appending events, committing transactions) that's going to reoccur
across basically all your command handlers. Likewise, it would be best if you could completely
isolate your business logic that *decides* what new events should be appended completely
away from the infrastructure code so that you can more easily reason about that code and
easily test that business logic. To that end, Wolverine supports the [Decider](https://thinkbeforecoding.com/post/2021/12/17/functional-event-sourcing-decider)
pattern with Marten using the `[MartenCommandWorkflow]` middleware.
Using that middleware, we get this slimmer code:

<!-- snippet: sample_MarkItemReadyHandler -->
<a id='snippet-sample_markitemreadyhandler'></a>
```cs
[AggregateHandler]
public static IEnumerable<object> Handle(MarkItemReady command, Order order)
{
    if (order.Items.TryGetValue(command.ItemName, out var item))
    {
        // Not doing this in a purist way here, but just
        // trying to illustrate the Wolverine mechanics
        item.Ready = true;

        // Mark that the this item is ready
        yield return new ItemReady(command.ItemName);
    }
    else
    {
        // Some crude validation
        throw new InvalidOperationException($"Item {command.ItemName} does not exist in this order");
    }

    // If the order is ready to ship, also emit an OrderReady event
    if (order.IsReadyToShip())
    {
        yield return new OrderReady();
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/OrderEventSourcingSample/Order.cs#L252-L279' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_markitemreadyhandler' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

In the case above, Wolverine is wrapping middleware around our basic command handler to
to:

1. Fetch the appropriate `Order` aggregate matching the command
2. Append any new events returned from the handle method to the Marten event stream
   for this `Order`
3. Saves any outstanding changes and commits the Marten unit of work

To make this more clear, here's the generated code (with some reformatting and extra comments):

<!-- snippet: sample_generated_MarkItemReadyHandler -->
<a id='snippet-sample_generated_markitemreadyhandler'></a>
```cs
public class MarkItemReadyHandler1442193977 : MessageHandler
{
    private readonly OutboxedSessionFactory _outboxedSessionFactory;

    public MarkItemReadyHandler1442193977(OutboxedSessionFactory outboxedSessionFactory)
    {
        _outboxedSessionFactory = outboxedSessionFactory;
    }

    public override async Task HandleAsync(MessageContext context, CancellationToken cancellation)
    {
        var markItemReady = (MarkItemReady)context.Envelope.Message;
        await using var documentSession = _outboxedSessionFactory.OpenSession(context);
        var eventStore = documentSession.Events;
        // Loading Marten aggregate
        var eventStream = await eventStore.FetchForWriting<Order>(markItemReady.OrderId, markItemReady.Version, cancellation).ConfigureAwait(false);

        var outgoing1 = MarkItemReadyHandler.Handle(markItemReady, eventStream.Aggregate);
        if (outgoing1 != null)
        {
            // Capturing any possible events returned from the command handlers
            eventStream.AppendMany(outgoing1);

        }

        await documentSession.SaveChangesAsync(cancellation).ConfigureAwait(false);
    }

}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/OrderEventSourcingSample/Internal/Generated/JasperHandlers/MarkItemReadyHandler1442193977.cs.cs#L13-L45' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_generated_markitemreadyhandler' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

As you probably guessed, there are some naming conventions or other questions you need to be aware of
before you use this middleware strategy.

### Handler Method Signatures

The Marten workflow command handler method signature needs to follow these rules:

* Either explicitly use the `[MartenCommandWorkflow]` attribute on the handler method **or use the `AggregateHandler` suffix** on the message handler type to tell Wolverine to opt into the aggregate command workflow.
* The first argument should be the command type, just like any other Wolverine message handler
* The 2nd argument should be the aggregate -- either the aggregate itself (`Order`) or wrapped
  in the Marten `IEventStream<T>` type (`IEventStream<Order>`). There is an example of that usage below:

<!-- snippet: sample_MarkItemReadyHandler_with_explicit_stream -->
<a id='snippet-sample_markitemreadyhandler_with_explicit_stream'></a>
```cs
[AggregateHandler]
public static void Handle(OrderEventSourcingSample.MarkItemReady command, IEventStream<Order> stream)
{
    var order = stream.Aggregate;

    if (order.Items.TryGetValue(command.ItemName, out var item))
    {
        // Not doing this in a purist way here, but just
        // trying to illustrate the Wolverine mechanics
        item.Ready = true;

        // Mark that the this item is ready
        stream.AppendOne(new ItemReady(command.ItemName));
    }
    else
    {
        // Some crude validation
        throw new InvalidOperationException($"Item {command.ItemName} does not exist in this order");
    }

    // If the order is ready to ship, also emit an OrderReady event
    if (order.IsReadyToShip())
    {
        stream.AppendOne(new OrderReady());
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/OrderEventSourcingSample/Alternatives/Signatures.cs#L25-L54' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_markitemreadyhandler_with_explicit_stream' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Just as in other Wolverine [message handlers](/guide/handlers/), you can use
additional method arguments for registered services ("method injection"), the `CancellationToken`
for the message, and the message `Envelope` if you need access to message metadata.

As for the return values from these handler methods, you can use:

* It's legal to have **no** return values if you are directly using `IEventStream<T>` to append events
* `IEnumerable<object>` or `object[]` to denote that a value is events to append to the current event stream
* `IAsyncEnumerable<object` will also be treated as a variable enumerable to events to append to the current event stream
* `Wolverine.Events` to denote a list of events. You *may* find this to lead to more readable code in some cases
* `OutgoingMessages` to refer to additional command messages to be published that should *not* be captured as events
* `ISideEffect` objects
* Any other type would be considered to be a separate event type, and you may happily use that for either a single event
  or a tuple of separate events that will be appended to the event stream

Here's an alternative of the `MarkItemReady` handler that uses `Events`:

<!-- snippet: sample_using_events_and_messages_from_MartenCommandWorkflow -->
<a id='snippet-sample_using_events_and_messages_from_martencommandworkflow'></a>
```cs
[AggregateHandler]
public static async Task<(Events, OutgoingMessages)> HandleAsync(MarkItemReady command, Order order, ISomeService service)
{
    // All contrived, let's say we need to call some 
    // kind of service to get data so this handler has to be
    // async
    var data = await service.FindDataAsync();

    var messages = new OutgoingMessages();
    var events = new Events();
    
    if (order.Items.TryGetValue(command.ItemName, out var item))
    {
        // Not doing this in a purist way here, but just
        // trying to illustrate the Wolverine mechanics
        item.Ready = true;

        // Mark that the this item is ready
        events += new ItemReady(command.ItemName);
    }
    else
    {
        // Some crude validation
        throw new InvalidOperationException($"Item {command.ItemName} does not exist in this order");
    }

    // If the order is ready to ship, also emit an OrderReady event
    if (order.IsReadyToShip())
    {
        events += new OrderReady();
        messages.Add(new ShipOrder(order.Id));
    }

    // This results in both new events being captured
    // and potentially the ShipOrder message going out
    return (events, messages);
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/OrderEventSourcingSample/Order.cs#L293-L333' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_events_and_messages_from_martencommandworkflow' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->



### Determining the Aggregate Identity

Wolverine is trying to determine a public member on the command type that refers to the identity
of the aggregate type. You've got two options, either use the implied naming convention below
where the `OrderId` property is assumed to be the identity of the `Order` aggregate
by appending "Id" to the aggregate type name (it's not case sensitive if you were wondering):

<!-- snippet: sample_MarkItemReady -->
<a id='snippet-sample_markitemready'></a>
```cs
// OrderId refers to the identity of the Order aggregate
public record MarkItemReady(Guid OrderId, string ItemName, int Version);
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/OrderEventSourcingSample/Order.cs#L65-L70' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_markitemready' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Or if you want to use a different member, bypass the convention, or just don't like conventional
magic, you can decorate a public member
on the command class with Marten's `[Identity]` attribute like so:

<!-- snippet: sample_MarkItemReady_with_explicit_identity -->
<a id='snippet-sample_markitemready_with_explicit_identity'></a>
```cs
public class MarkItemReady
{
    // This attribute tells Wolverine that this property will refer to the
    // Order aggregate
    [Identity] public Guid Id { get; init; }

    public string ItemName { get; init; }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/OrderEventSourcingSample/Alternatives/Signatures.cs#L8-L19' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_markitemready_with_explicit_identity' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Publishing Events

::: tip
This functionality is *brand spanking new* and will likely be enhanced after user feedback.
:::

::: warning
The Wolverine team recommends against combining this functionality with **also** using events as either a handler response
or cascaded messages as the behavior can easily become confusing. Instead, prefer using custom types for handler responses
instead of the raw event types when using the event forwarding.
:::

You can also opt to automatically publish any event types captured by Marten through Wolverine's persistent
outbox. Do note that only event types that have a matching subscription in the Wolverine configuration
will actually be published.

To opt into this feature, chain the Wolverine `AddMarten().EventForwardingToWolverine()` call as
shown in this application bootstrapping sample shown below:

<!-- snippet: sample_opting_into_wolverine_event_publishing -->
<a id='snippet-sample_opting_into_wolverine_event_publishing'></a>
```cs
builder.Services.AddMarten(opts =>
    {
        var connString = builder
            .Configuration
            .GetConnectionString("marten");

        opts.Connection(connString);

        // There will be more here later...

        opts.Projections
            .Add<AppointmentDurationProjection>(ProjectionLifecycle.Async);

        // OR ???

        opts.Projections
            .Add<AppointmentDurationProjection>(ProjectionLifecycle.Inline);

        opts.Projections.Add<AppointmentProjection>(ProjectionLifecycle.Inline);
        opts.Projections
            .SelfAggregate<ProviderShift>(ProjectionLifecycle.Async);
    })

    // This adds a hosted service to run
    // asynchronous projections in a background process
    .AddAsyncDaemon(DaemonMode.HotCold)

    // I added this to enroll Marten in the Wolverine outbox
    .IntegrateWithWolverine()

    // I also added this to opt into events being forward to
    // the Wolverine outbox during SaveChangesAsync()
    .EventForwardingToWolverine();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/CQRSWithMarten/TeleHealth.WebApi/Program.cs#L45-L81' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_opting_into_wolverine_event_publishing' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

This does need to be paired with a little bit of Wolverine configuration to add
subscriptions to event types like so:

<!-- snippet: sample_configuring_wolverine_event_subscriptions -->
<a id='snippet-sample_configuring_wolverine_event_subscriptions'></a>
```cs
builder.Host.UseWolverine(opts =>
{
    // I'm choosing to process any ChartingFinished event messages
    // in a separate, local queue with persistent messages for the inbox/outbox
    opts.PublishMessage<ChartingFinished>()
        .ToLocalQueue("charting")
        .UseDurableInbox();

    // If we encounter a concurrency exception, just try it immediately
    // up to 3 times total
    opts.Policies.OnException<ConcurrencyException>().RetryTimes(3);

    // It's an imperfect world, and sometimes transient connectivity errors
    // to the database happen
    opts.Policies.OnException<NpgsqlException>()
        .RetryWithCooldown(50.Milliseconds(), 100.Milliseconds(), 250.Milliseconds());
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/CQRSWithMarten/TeleHealth.WebApi/Program.cs#L17-L37' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_configuring_wolverine_event_subscriptions' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->
