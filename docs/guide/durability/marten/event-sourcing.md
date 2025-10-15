# Aggregate Handlers and Event Sourcing

::: tip
Only use the "aggregate handler workflow" if you are wanting to potentially write new events to an existing event stream. If all you
need in a message handler or HTTP endpoint is a read-only copy of an event streamed aggregate from Marten, use the `[ReadAggregate]` attribute
instead that has a little bit lighter weight runtime within Marten.
:::

::: info
If your message handler or HTTP endpoint uses more than one declarative attribute for retrieving Marten data,
Wolverine 5.0+ is able to utilize [Marten's Batch Querying capability](https://martendb.io/documents/querying/batched-queries.html#batched-queries) for more efficient interaction with the database.

This batching behavior is also supported for all the declarative attributes and the "aggregate handler workflow" in general
described in this page.
:::

See the [OrderEventSourcingSample project on GitHub](https://github.com/JasperFx/wolverine/tree/main/src/Persistence/OrderEventSourcingSample) for more samples.

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/OrderEventSourcingSample/Order.cs#L22-L66' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_order_event_sourced_aggregate' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

At a minimum, we're going to want a command handler for this command message that marks an order item as ready to ship and then evaluates whether
or not based on the current state of the `Order` aggregate whether or not the logical order is ready to be shipped out:

<!-- snippet: sample_MarkItemReady -->
<a id='snippet-sample_markitemready'></a>
```cs
// OrderId refers to the identity of the Order aggregate
public record MarkItemReady(Guid OrderId, string ItemName, int Version);
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/OrderEventSourcingSample/Order.cs#L68-L73' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_markitemready' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

In the code above, we're also utilizing Wolverine's [outbox messaging](/guide/durability/) support to both order and guarantee the delivery of a `ShipOrder` message when
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/OrderEventSourcingSample/Order.cs#L77-L128' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_markitemcontroller' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Hopefully, that code is easy to understand, but there's some potentially repetitive code
(loading aggregates, appending events, committing transactions) that will reoccur
across all your command handlers. Likewise, it would be best to completely
isolate your business logic that *decides* what new events should be appended completely
away from the infrastructure code so that you can more easily reason about that code and
easily test that business logic. To that end, Wolverine supports the [Decider](https://thinkbeforecoding.com/post/2021/12/17/functional-event-sourcing-decider)
pattern with Marten using the `[AggregateHandler]` middleware.
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/OrderEventSourcingSample/Order.cs#L254-L281' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_markitemreadyhandler' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

In the case above, Wolverine is wrapping middleware around our basic command handler to
to:

1. Fetch the appropriate `Order` aggregate matching the command
2. Append any new events returned from the handle method to the Marten event stream
   for this `Order`
3. Saves any outstanding changes and commits the Marten unit of work

To make this more clear, here's the generated code (with some reformatting and extra comments):


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

As you probably guessed, there are some naming conventions or other questions you need to be aware of
before you use this middleware strategy.

::: warning
There are some open, let's call them _imperfections_ with Wolverine's code generation against the `[WriteAggregate]` and `[ReadAggregate]`
usage. For best results, only use these attributes on a parameter within the main HTTP endpoint method and not in `Validate/Before/Load` methods.
:::

::: info
The `[Aggregate]` and `[WriteAggregate]` attributes _require the requested stream and aggregate to be found by default_, meaning that the handler or HTTP
endpoint will be stopped if the requested data is not found. You can explicitly mark individual attributes as `Required=false`.
:::

Alternatively, there is also the newer `[WriteAggregate]` usage, with this example being a functional alternative
mark up:

<!-- snippet: sample_MarkItemReadyHandler_with_WriteAggregate -->
<a id='snippet-sample_markitemreadyhandler_with_writeaggregate'></a>
```cs
public static IEnumerable<object> Handle(
    // The command
    MarkItemReady command, 
    
    // This time we'll mark the parameter as the "aggregate"
    [WriteAggregate] Order order)
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/OrderEventSourcingSample/Order.cs#L286-L317' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_markitemreadyhandler_with_writeaggregate' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The `[WriteAggregate]` attribute also opts into the "aggregate handler workflow", but is placed at the parameter level
instead of the class level. This was added to extend the "aggregate handler workflow" to operations that involve multiple
event streams in one transaction. 

::: tip
`[WriteAggregate]` works equally on message handlers as it does on HTTP endpoints. In fact, the older `[Aggregate]` attribute
in Wolverine.Http.Marten is now just a subclass of `[WriteAggregate]`.
:::

## Validation on Stream Existence <Badge type="tip" text="4.8" />

By default, the "aggregate handler workflow" does no validation on whether or not the identified event stream actually 
exists at runtime, and it's possible to receive a null for the aggregate in this example if the aggregate does not exist:

<!-- snippet: sample_MarkItemReadyHandler_with_WriteAggregate -->
<a id='snippet-sample_markitemreadyhandler_with_writeaggregate'></a>
```cs
public static IEnumerable<object> Handle(
    // The command
    MarkItemReady command, 
    
    // This time we'll mark the parameter as the "aggregate"
    [WriteAggregate] Order order)
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/OrderEventSourcingSample/Order.cs#L286-L317' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_markitemreadyhandler_with_writeaggregate' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

As long as you handle the case where the requested is null, you can even effectively start a new stream by emitting events
from your handler or HTTP endpoint. 

If you do want to protect message handlers or HTTP endpoints from acting on missing streams because of bad user inputs
(or who knows what, it's a chaotic world and you should never trust your system is receiving valid input), you now have 
some options to mark the aggregate itself as required and even control how Wolverine deals with the aggregate being missing 
as shown in these sample signatures below:

<!-- snippet: sample_validation_on_aggregate_being_missing_in_aggregate_handler_workflow -->
<a id='snippet-sample_validation_on_aggregate_being_missing_in_aggregate_handler_workflow'></a>
```cs
public static class ValidatedMarkItemReadyHandler
{
    public static IEnumerable<object> Handle(
        // The command
        MarkItemReady command,

        // In HTTP this will return a 404 status code and stop
        // the request if the Order is not found
        
        // In message handlers, this will log that the Order was not found,
        // then stop processing. The message would be effectively
        // discarded
        [WriteAggregate(Required = true)] Order order) => [];

    [WolverineHandler]
    public static IEnumerable<object> Handle2(
        // The command
        MarkItemReady command,

        // In HTTP this will return a 400 status code and 
        // write out a ProblemDetails response with a default message explaining
        // the data that could not be found
        [WriteAggregate(Required = true, OnMissing = OnMissing.ProblemDetailsWith400)] Order order) => [];
    
    [WolverineHandler]
    public static IEnumerable<object> Handle3(
        // The command
        MarkItemReady command,

        // In HTTP this will return a 404 status code and 
        // write out a ProblemDetails response with a default message explaining
        // the data that could not be found
        [WriteAggregate(Required = true, OnMissing = OnMissing.ProblemDetailsWith404)] Order order) => [];

    
    [WolverineHandler]
    public static IEnumerable<object> Handle4(
        // The command
        MarkItemReady command,

        // In HTTP this will return a 400 status code and 
        // write out a ProblemDetails response with a custom message.
        // Wolverine will substitute in the order identity into the message for "{0}"
        // In message handlers, Wolverine will log using your custom message then discard the message
        [WriteAggregate(Required = true, OnMissing = OnMissing.ProblemDetailsWith404, MissingMessage = "Cannot find Order {0}")] Order order) => [];

}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/OrderEventSourcingSample/Order.cs#L372-L422' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_validation_on_aggregate_being_missing_in_aggregate_handler_workflow' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The `Required`, `OnMissing`, and `MissingMessage` properties behave consistently on all Wolverine attributes
like `[Entity]` or `[WriteAggregate]` or `[ReadAggregate]`.

### Handler Method Signatures

The Marten workflow command handler method signature needs to follow these rules:

* Either explicitly use the `[AggregateHandler]` attribute on the handler method **or use the `AggregateHandler` suffix** on the message handler type to tell Wolverine to opt into the aggregate command workflow.
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/OrderEventSourcingSample/Alternatives/Signatures.cs#L26-L55' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_markitemreadyhandler_with_explicit_stream' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Just as in other Wolverine [message handlers](/guide/handlers/), you can use
additional method arguments for registered services ("method injection"), the `CancellationToken`
for the message, and the message `Envelope` if you need access to message metadata.

As for the return values from these handler methods, you can use:

* It's legal to have **no** return values if you are directly using `IEventStream<T>` to append events
* `IEnumerable<object>` or `object[]` to denote that a value is events to append to the current event stream
* `IAsyncEnumerable<object` will also be treated as a variable enumerable to events to append to the current event stream
* `Wolverine.Marten.Events` to denote a list of events. You *may* find this to lead to more readable code in some cases
* `OutgoingMessages` to refer to additional command messages to be published that should *not* be captured as events
* `ISideEffect` objects
* Any other type would be considered to be a separate event type, and you may happily use that for either a single event
  or a tuple of separate events that will be appended to the event stream

Here's an alternative to the `MarkItemReady` handler that uses `Events`:

<!-- snippet: sample_using_events_and_messages_from_AggregateHandler -->
<a id='snippet-sample_using_events_and_messages_from_aggregatehandler'></a>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/OrderEventSourcingSample/Order.cs#L329-L369' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_events_and_messages_from_aggregatehandler' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/OrderEventSourcingSample/Order.cs#L68-L73' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_markitemready' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/OrderEventSourcingSample/Alternatives/Signatures.cs#L9-L20' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_markitemready_with_explicit_identity' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Validation <Badge type="tip" text="4.8" />

Every possible attribute for triggering the "aggregate handler workflow" includes support for data requirements as
shown below with `[ReadAggregate]`: 

<!-- snippet: sample_read_aggregate_fine_grained_validation_control -->
<a id='snippet-sample_read_aggregate_fine_grained_validation_control'></a>
```cs
// Straight up 404 on missing
[WolverineGet("/letters1/{id}")]
public static LetterAggregate GetLetter1([ReadAggregate] LetterAggregate letters) => letters;

// Not required
[WolverineGet("/letters2/{id}")]
public static string GetLetter2([ReadAggregate(Required = false)] LetterAggregate letters)
{
    return letters == null ? "No Letters" : "Got Letters";
}

// Straight up 404 & problem details on missing
[WolverineGet("/letters3/{id}")]
public static LetterAggregate GetLetter3([ReadAggregate(OnMissing = OnMissing.ProblemDetailsWith404)] LetterAggregate letters) => letters;
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/Wolverine.Http.Tests/Marten/reacting_to_read_aggregate.cs#L144-L163' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_read_aggregate_fine_grained_validation_control' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Forwarding Events

See [Event Forwarding](./event-forwarding) for more information.

## Returning the Updated Aggregate <Badge type="tip" text="3.5" />

A common use case for the "aggregate handler workflow" has been to respond with the now updated state of the projected
aggregate that has just been updated by appending new events. Until now, that's effectively meant making a completely separate
call to the database through Marten to retrieve the latest updates. 

::: info
To understand more about the inner workings of the next section, see the Marten documentation on its [FetchLatest](https://martendb.io/events/projections/read-aggregates.html#fetchlatest)
API.
:::

As a quick tip for performance, assuming that you are *not* mutating the projected documents within your command
handlers, you can opt for this significant Marten optimization to eliminate extra database round trips while
using the aggregate handler workflow:

```csharp
builder.Services.AddMarten(opts =>
{
    // Other Marten configuration

    // Use this setting to get the very best performance out
    // of the UpdatedAggregate workflow and aggregate handler
    // workflow over all
    opts.Events.UseIdentityMapForAggregates = true;
}).IntegrateWithWolverine();
```

::: info
The setting above cannot be a default in Marten because it can break some existing code with a very different
workflow than what the Critter Stack team recommends for the aggregate handler workflow.
:::

Wolverine.Marten has a special response type for message handlers or HTTP endpoints we can use as a directive to tell Wolverine
to respond with the latest state of a projected aggregate as part of the command execution. Let's make this concrete by
taking the `MarkItemReady` command handler we've used earlier in this guide and building a slightly new version that
produces a response of the latest aggregate:

<!-- snippet: sample_MarkItemReadyHandler_with_response_for_updated_aggregate -->
<a id='snippet-sample_markitemreadyhandler_with_response_for_updated_aggregate'></a>
```cs
[AggregateHandler]
public static (
    // Just tells Wolverine to use Marten's FetchLatest API to respond with
    // the updated version of Order that reflects whatever events were appended
    // in this command
    UpdatedAggregate, 
    
    // The events that should be appended to the event stream for this order
    Events) Handle(OrderEventSourcingSample.MarkItemReady command, Order order)
{
    var events = new Events();
    
    if (order.Items.TryGetValue(command.ItemName, out var item))
    {
        // Not doing this in a purist way here, but just
        // trying to illustrate the Wolverine mechanics
        item.Ready = true;

        // Mark that the this item is ready
        events.Add(new ItemReady(command.ItemName));
    }
    else
    {
        // Some crude validation
        throw new InvalidOperationException($"Item {command.ItemName} does not exist in this order");
    }

    // If the order is ready to ship, also emit an OrderReady event
    if (order.IsReadyToShip())
    {
        events.Add(new OrderReady());
    }

    return (new UpdatedAggregate(), events);
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/OrderEventSourcingSample/Alternatives/Signatures.cs#L63-L101' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_markitemreadyhandler_with_response_for_updated_aggregate' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Note the usage of the `Wolverine.Marten.UpdatedAggregate` response in the handler. That type by itself is just a directive 
to Wolverine to generate the necessary code to call `FetchLatest` and respond with that. The command handler above allows
us to use the command in a mediator usage like so:

<!-- snippet: sample_using_UpdatedAggregate_with_invoke_async -->
<a id='snippet-sample_using_updatedaggregate_with_invoke_async'></a>
```cs
public static Task<Order> update_and_get_latest(IMessageBus bus, MarkItemReady command)
{
    // This will return the updated version of the Order
    // aggregate that incorporates whatever events were appended
    // in the course of processing the command
    return bus.InvokeAsync<Order>(command);
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/OrderEventSourcingSample/Alternatives/Signatures.cs#L103-L113' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_updatedaggregate_with_invoke_async' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Likewise, you can use `UpdatedAggregate` as the response body of an HTTP endpoint with Wolverine.HTTP [as shown here](/guide/http/marten.html#responding-with-the-updated-aggregate~~~~).

::: info
This feature has been more or less requested several times, but was finally brought about because of the need
to consume Wolverine + Marten commands within Hot Chocolate mutations and always return the current state of
the projected aggregate being updated to the user interface.
:::

### Passing the Aggregate to Before/Validate/Load Methods

The "[compound handler](/guide/handlers/#compound-handlers)" feature is a valuable way in Wolverine to organize your handler code, and fully supported
within the aggregate handler workflow as well. If you have a command handler method marked with `[AggregateHandler]` or
the `[Aggregate]` attribute in HTTP usage, you can also pass the aggregate type as an argument to any `Before` / `LoadAsync` / `Validate`
method on that handler to do validation before the main handler method. Here's a sample from the tests of doing just that:

<!-- snippet: sample_passing_aggregate_into_validate_method -->
<a id='snippet-sample_passing_aggregate_into_validate_method'></a>
```cs
public record RaiseIfValidated(Guid LetterAggregateId);

public static class RaiseIfValidatedHandler
{
    public static HandlerContinuation Validate(LetterAggregate aggregate) =>
        aggregate.ACount == 0 ? HandlerContinuation.Continue : HandlerContinuation.Stop;
    
    [AggregateHandler]
    public static IEnumerable<object> Handle(RaiseIfValidated command, LetterAggregate aggregate)
    {
        yield return new BEvent();
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/MartenTests/AggregateHandlerWorkflow/aggregate_handler_workflow.cs#L407-L423' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_passing_aggregate_into_validate_method' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Archiving Streams

To mark a Marten event stream as archived from a Wolverine aggregate handler, just append the special Marten [Archived](https://martendb.io/events/archiving.html#archived-event)
event to the stream just like you would in any other aggregate handler. 

## Reading the Latest Version of an Aggregate

::: info
This is using Marten's [FetchLatest](https://martendb.io/events/projections/read-aggregates.html#fetchlatest) API and is limited to single stream
projections.
:::

If you want to inject the current state of an event sourced aggregate as a parameter into
a message handler method strictly for information and don't need the heavier "aggregate handler workflow," use the `[ReadAggregate]` attribute like this:

<!-- snippet: sample_using_ReadAggregate_in_messsage_handlers -->
<a id='snippet-sample_using_readaggregate_in_messsage_handlers'></a>
```cs
public record FindAggregate(Guid Id);

public static class FindLettersHandler
{
    // This is admittedly just some weak sauce testing support code
    public static LetterAggregateEnvelope Handle(FindAggregate command, [ReadAggregate] LetterAggregate aggregate)
    {
        return new LetterAggregateEnvelope(aggregate);
    }
    
    /* ALTERNATIVE VERSION
    [WolverineHandler]
    public static LetterAggregateEnvelope Handle2(
        FindAggregate command, 
        
        // Just showing you that you can disable the validation
        [ReadAggregate(Required = false)] LetterAggregate aggregate)
    {
        return aggregate == null ? null : new LetterAggregateEnvelope(aggregate);
    }
    */
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/MartenTests/read_aggregate_attribute_usage.cs#L81-L106' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_readaggregate_in_messsage_handlers' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

If the aggregate doesn't exist, the HTTP request will stop with a 404 status code.
The aggregate/stream identity is found with the same rules as the `[Entity]` or `[Aggregate]` attributes:

1. You can specify a particular request body property name or route argument
2. Look for a request body property or route argument named "EntityTypeId"
3. Look for a request body property or route argument named "Id" or "id"

You can override the validation rules for how Wolverine handles an aggregate / event stream not being found
by setting these properties on `[ReadAttribute]` (which is much more useful for HTTP endpoints):

<!-- snippet: sample_read_aggregate_fine_grained_validation_control -->
<a id='snippet-sample_read_aggregate_fine_grained_validation_control'></a>
```cs
// Straight up 404 on missing
[WolverineGet("/letters1/{id}")]
public static LetterAggregate GetLetter1([ReadAggregate] LetterAggregate letters) => letters;

// Not required
[WolverineGet("/letters2/{id}")]
public static string GetLetter2([ReadAggregate(Required = false)] LetterAggregate letters)
{
    return letters == null ? "No Letters" : "Got Letters";
}

// Straight up 404 & problem details on missing
[WolverineGet("/letters3/{id}")]
public static LetterAggregate GetLetter3([ReadAggregate(OnMissing = OnMissing.ProblemDetailsWith404)] LetterAggregate letters) => letters;
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/Wolverine.Http.Tests/Marten/reacting_to_read_aggregate.cs#L144-L163' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_read_aggregate_fine_grained_validation_control' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

There is also an option with `OnMissing` to throw a `RequiredDataMissingException` exception if a required data element
is missing. This option is probably most useful with message handlers where you may want to key off the exception with custom
error handling rules.

## Targeting Multiple Streams at Once <Badge type="tip" text="4.9.0" />

It's now possible to use the "aggregate handler workflow" while needing to append events to more
than one event stream at a time.

::: tip
You can use read only views of event streams through `[ReadAggregate]` at will, and that will use
Marten's `FetchLatest()` API underneath. For appending to multiple streams though, for now you will 
have to directly target `IEventStream<T>` to help Marten know which stream you're appending events to.
:::

Using the canonical example of a use case where you move money from one account to another account and need both
changes to be persisted in one atomic transaction. Let’s start with a simplified domain model of
events and a “self-aggregating” Account type like this:

<!-- snippet: sample_account_domain_code -->
<a id='snippet-sample_account_domain_code'></a>
```cs
public record AccountCreated(double InitialAmount);
public record Debited(double Amount);
public record Withdrawn(double Amount);

public class Account
{
    public Guid Id { get; set; }
    public double Amount { get; set; }

    public static Account Create(IEvent<AccountCreated> e)
        => new Account { Id = e.StreamId, Amount = e.Data.InitialAmount};

    public void Apply(Debited e) => Amount += e.Amount;
    public void Apply(Withdrawn e) => Amount -= e.Amount;
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Accounts/AccountCode.cs#L9-L27' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_account_domain_code' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

And you need to handle a command like this:

<!-- snippet: sample_TransferMoney_command -->
<a id='snippet-sample_transfermoney_command'></a>
```cs
public record TransferMoney(Guid FromId, Guid ToId, double Amount);
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Accounts/AccountCode.cs#L29-L33' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_transfermoney_command' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Using the `[WriteAggregate]` attribute to denote the event streams we need to work with,
we could write this message handler + HTTP endpoint:

<!-- snippet: sample_TransferMoneyEndpoint -->
<a id='snippet-sample_transfermoneyendpoint'></a>
```cs
public static class TransferMoneyHandler    
{
    [WolverinePost("/accounts/transfer")]
    public static void Handle(
        TransferMoney command,

        [WriteAggregate(nameof(TransferMoney.FromId))] IEventStream<Account> fromAccount,
        
        [WriteAggregate(nameof(TransferMoney.ToId))] IEventStream<Account> toAccount)
    {
        // Would already 404 if either referenced account does not exist
        if (fromAccount.Aggregate.Amount >= command.Amount)
        {
            fromAccount.AppendOne(new Withdrawn(command.Amount));
            toAccount.AppendOne(new Debited(command.Amount));
        }
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Accounts/AccountCode.cs#L35-L56' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_transfermoneyendpoint' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The `IEventStream<T>` abstraction comes from Marten’s `FetchForWriting()` API that is our
recommended way to interact with Marten streams in typical command handlers. This
API is used underneath Wolverine’s “aggregate handler workflow”, but normally hidden
from user written code if you’re only working with one stream at a time. In this case
though, we’ll need to work with the raw `IEventStream<T>` objects that both wrap the
projected aggregation of each Account as well as providing a point where we can explicitly
append events separately to each event stream. `FetchForWriting()` guarantees that you get
the most up to date information for the Account view of each event stream regardless of
how you have configured Marten’s `ProjectionLifecycle` for `Account` (kind of an important
detail here!).

The typical Marten transactional middleware within Wolverine is calling `SaveChangesAsync()`
for us on the Marten unit of work IDocumentSession for the command. If there’s enough funds in
the “From” account, this command will append a `Withdrawn` event to the “From” account and
a `Debited` event to the “To” account. If either account has been written to between
fetching the original information, Marten will reject the changes and throw its
`ConcurrencyException` as an optimistic concurrency check.

In unit testing, we could write a unit test for the “happy path” where you have enough funds to cover the transfer like this:

<!-- snippet: sample_when_transfering_money -->
<a id='snippet-sample_when_transfering_money'></a>
```cs
public class when_transfering_money
{
    [Fact]
    public void happy_path_have_enough_funds()
    {
        // StubEventStream<T> is a type that was recently added to Marten
        // specifically to facilitate testing logic like this
        var fromAccount = new StubEventStream<Account>(new Account { Amount = 1000 })
        {
            Id = Guid.NewGuid()
        };
        
        var toAccount = new StubEventStream<Account>(new Account { Amount = 100})
        {
            Id = Guid.NewGuid()
        };
        
        TransferMoneyHandler.Handle(new TransferMoney(fromAccount.Id, toAccount.Id, 100), fromAccount, toAccount);

        // Now check the events we expected to be appended
        fromAccount.Events.Single().Data.ShouldBeOfType<Withdrawn>().Amount.ShouldBe(100);
        toAccount.Events.Single().Data.ShouldBeOfType<Debited>().Amount.ShouldBe(100);
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/Wolverine.Http.Tests/Marten/working_against_multiple_streams.cs#L105-L132' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_when_transfering_money' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->
