# Aggregate Handlers and Event Sourcing

::: tip
Only use the "aggregate handler workflow" if you are wanting to potentially write new events to an existing event stream. If all you
need in a message handler or HTTP endpoint is a read-only copy of an event streamed aggregate from Polecat, use the `[ReadAggregate]` attribute
instead that has a little bit lighter weight runtime within Polecat.
:::

The Wolverine + Polecat combination is optimized for efficient and productive development using a [CQRS architecture style](https://martinfowler.com/bliki/CQRS.html) with Polecat's event sourcing support.
Specifically, let's dive into the responsibilities of a typical command handler in a CQRS with event sourcing architecture:

1. Fetch any current state of the system that's necessary to evaluate or validate the incoming event
2. *Decide* what events should be emitted and captured in response to an incoming event
3. Manage concurrent access to system state
4. Safely commit the new events
5. Selectively publish some of the events based on system needs to other parts of your system or even external systems
6. Instrument all of the above

And then lastly, you're going to want some resiliency and selective retry capabilities for concurrent access violations or just normal infrastructure hiccups.

Let's jump right into an example order management system. I'm going to model the order workflow with this aggregate model:

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

    // This is important, by convention this would
    // be the version
    public int Version { get; set; }

    public DateTimeOffset? Shipped { get; private set; }

    public Dictionary<string, Item> Items { get; set; } = new();

    // These methods are used by Polecat to update the aggregate
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

At a minimum, we're going to want a command handler for this command message that marks an order item as ready to ship:

```cs
// OrderId refers to the identity of the Order aggregate
public record MarkItemReady(Guid OrderId, string ItemName, int Version);
```

Wolverine supports the [Decider](https://thinkbeforecoding.com/post/2021/12/17/functional-event-sourcing-decider)
pattern with Polecat using the `[AggregateHandler]` middleware.
Using that middleware, we get this slim code:

```cs
[AggregateHandler]
public static IEnumerable<object> Handle(MarkItemReady command, Order order)
{
    if (order.Items.TryGetValue(command.ItemName, out var item))
    {
        item.Ready = true;

        // Mark that the this item is ready
        yield return new ItemReady(command.ItemName);
    }
    else
    {
        throw new InvalidOperationException($"Item {command.ItemName} does not exist in this order");
    }

    // If the order is ready to ship, also emit an OrderReady event
    if (order.IsReadyToShip())
    {
        yield return new OrderReady();
    }
}
```

In the case above, Wolverine is wrapping middleware around our basic command handler to:

1. Fetch the appropriate `Order` aggregate matching the command
2. Append any new events returned from the handle method to the Polecat event stream for this `Order`
3. Saves any outstanding changes and commits the Polecat unit of work

::: warning
There are some open imperfections with Wolverine's code generation against the `[WriteAggregate]` and `[ReadAggregate]`
usage. For best results, only use these attributes on a parameter within the main HTTP endpoint method and not in `Validate/Before/Load` methods.
:::

::: info
The `[Aggregate]` and `[WriteAggregate]` attributes _require the requested stream and aggregate to be found by default_, meaning that the handler or HTTP
endpoint will be stopped if the requested data is not found. You can explicitly mark individual attributes as `Required=false`.
:::

Alternatively, there is also the newer `[WriteAggregate]` usage:

```cs
public static IEnumerable<object> Handle(
    MarkItemReady command,
    [WriteAggregate] Order order)
{
    if (order.Items.TryGetValue(command.ItemName, out var item))
    {
        item.Ready = true;
        yield return new ItemReady(command.ItemName);
    }
    else
    {
        throw new InvalidOperationException($"Item {command.ItemName} does not exist in this order");
    }

    if (order.IsReadyToShip())
    {
        yield return new OrderReady();
    }
}
```

The `[WriteAggregate]` attribute also opts into the "aggregate handler workflow", but is placed at the parameter level
instead of the class level. This was added to extend the "aggregate handler workflow" to operations that involve multiple
event streams in one transaction.

::: tip
`[WriteAggregate]` works equally on message handlers as it does on HTTP endpoints.
:::

## Validation on Stream Existence

By default, the "aggregate handler workflow" does no validation on whether or not the identified event stream actually
exists at runtime. You can protect against missing streams:

```cs
public static class ValidatedMarkItemReadyHandler
{
    public static IEnumerable<object> Handle(
        MarkItemReady command,

        // In HTTP this will return a 404 status code and stop
        // In message handlers, this will log and discard the message
        [WriteAggregate(Required = true)] Order order) => [];

    [WolverineHandler]
    public static IEnumerable<object> Handle2(
        MarkItemReady command,
        [WriteAggregate(Required = true, OnMissing = OnMissing.ProblemDetailsWith400)] Order order) => [];

    [WolverineHandler]
    public static IEnumerable<object> Handle3(
        MarkItemReady command,
        [WriteAggregate(Required = true, OnMissing = OnMissing.ProblemDetailsWith404)] Order order) => [];

    [WolverineHandler]
    public static IEnumerable<object> Handle4(
        MarkItemReady command,
        [WriteAggregate(Required = true, OnMissing = OnMissing.ProblemDetailsWith404, MissingMessage = "Cannot find Order {0}")] Order order) => [];
}
```

### Handler Method Signatures

The aggregate workflow command handler method signature needs to follow these rules:

* Either explicitly use the `[AggregateHandler]` attribute on the handler method **or use the `AggregateHandler` suffix** on the message handler type
* The first argument should be the command type
* The 2nd argument should be the aggregate -- either the aggregate itself (`Order`) or wrapped in the `IEventStream<T>` type (`IEventStream<Order>`):

```cs
[AggregateHandler]
public static void Handle(MarkItemReady command, IEventStream<Order> stream)
{
    var order = stream.Aggregate;

    if (order.Items.TryGetValue(command.ItemName, out var item))
    {
        item.Ready = true;
        stream.AppendOne(new ItemReady(command.ItemName));
    }
    else
    {
        throw new InvalidOperationException($"Item {command.ItemName} does not exist in this order");
    }

    if (order.IsReadyToShip())
    {
        stream.AppendOne(new OrderReady());
    }
}
```

As for the return values from these handler methods, you can use:

* It's legal to have **no** return values if you are directly using `IEventStream<T>` to append events
* `IEnumerable<object>` or `object[]` to denote events to append to the current event stream
* `IAsyncEnumerable<object>` will also be treated as events to append
* `Events` to denote a list of events
* `OutgoingMessages` to refer to additional command messages to be published that should *not* be captured as events
* `ISideEffect` objects
* Any other type would be considered to be a separate event type

Here's an alternative using `Events`:

```cs
[AggregateHandler]
public static async Task<(Events, OutgoingMessages)> HandleAsync(MarkItemReady command, Order order, ISomeService service)
{
    var data = await service.FindDataAsync();

    var messages = new OutgoingMessages();
    var events = new Events();

    if (order.Items.TryGetValue(command.ItemName, out var item))
    {
        item.Ready = true;
        events += new ItemReady(command.ItemName);
    }
    else
    {
        throw new InvalidOperationException($"Item {command.ItemName} does not exist in this order");
    }

    if (order.IsReadyToShip())
    {
        events += new OrderReady();
        messages.Add(new ShipOrder(order.Id));
    }

    return (events, messages);
}
```

### Determining the Aggregate Identity

Wolverine is trying to determine a public member on the command type that refers to the identity
of the aggregate type. You've got two options, either use the implied naming convention
where the `OrderId` property is assumed to be the identity of the `Order` aggregate:

```cs
// OrderId refers to the identity of the Order aggregate
public record MarkItemReady(Guid OrderId, string ItemName, int Version);
```

Or decorate a public member on the command class with the `[Identity]` attribute:

```cs
public class MarkItemReady
{
    [Identity] public Guid Id { get; init; }
    public string ItemName { get; init; }
}
```

## Forwarding Events

See [Event Forwarding](./event-forwarding) for more information.

## Returning the Updated Aggregate

A common use case has been to respond with the now updated state of the projected
aggregate that has just been updated by appending new events.

Wolverine.Polecat has a special response type for message handlers or HTTP endpoints we can use as a directive to tell Wolverine
to respond with the latest state of a projected aggregate as part of the command execution:

```cs
[AggregateHandler]
public static (UpdatedAggregate, Events) Handle(MarkItemReady command, Order order)
{
    var events = new Events();

    if (order.Items.TryGetValue(command.ItemName, out var item))
    {
        item.Ready = true;
        events.Add(new ItemReady(command.ItemName));
    }
    else
    {
        throw new InvalidOperationException($"Item {command.ItemName} does not exist in this order");
    }

    if (order.IsReadyToShip())
    {
        events.Add(new OrderReady());
    }

    return (new UpdatedAggregate(), events);
}
```

The `UpdatedAggregate` type is just a directive to Wolverine to generate the necessary code to call `FetchLatest` and respond with that:

```cs
public static Task<Order> update_and_get_latest(IMessageBus bus, MarkItemReady command)
{
    return bus.InvokeAsync<Order>(command);
}
```

You can also use `UpdatedAggregate` as the response body of an HTTP endpoint with Wolverine.HTTP [as shown here](/guide/http/polecat#responding-with-the-updated-aggregate).

### Passing the Aggregate to Before/Validate/Load Methods

The "[compound handler](/guide/handlers/#compound-handlers)" feature is fully supported within the aggregate handler workflow. You can pass the aggregate type as an argument to any `Before` / `LoadAsync` / `Validate` method:

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

## Reading the Latest Version of an Aggregate

If you want to inject the current state of an event sourced aggregate as a parameter into
a message handler method strictly for information and don't need the heavier "aggregate handler workflow," use the `[ReadAggregate]` attribute:

```cs
public record FindAggregate(Guid Id);

public static class FindLettersHandler
{
    public static LetterAggregateEnvelope Handle(FindAggregate command, [ReadAggregate] LetterAggregate aggregate)
    {
        return new LetterAggregateEnvelope(aggregate);
    }
}
```

If the aggregate doesn't exist, the HTTP request will stop with a 404 status code.
The aggregate/stream identity is found with these rules:

1. You can specify a particular request body property name or route argument
2. Look for a request body property or route argument named "EntityTypeId"
3. Look for a request body property or route argument named "Id" or "id"

## Targeting Multiple Streams at Once

It's possible to use the "aggregate handler workflow" while needing to append events to more than one event stream at a time.

::: tip
You can use read only views of event streams through `[ReadAggregate]` at will, and that will use
Polecat's `FetchLatest()` API underneath. For appending to multiple streams, use `IEventStream<T>` directly.
:::

```cs
public record TransferMoney(Guid FromId, Guid ToId, double Amount);

public static class TransferMoneyHandler
{
    [WolverinePost("/accounts/transfer")]
    public static void Handle(
        TransferMoney command,

        [WriteAggregate(nameof(TransferMoney.FromId))] IEventStream<Account> fromAccount,

        [WriteAggregate(nameof(TransferMoney.ToId))] IEventStream<Account> toAccount)
    {
        if (fromAccount.Aggregate.Amount >= command.Amount)
        {
            fromAccount.AppendOne(new Withdrawn(command.Amount));
            toAccount.AppendOne(new Debited(command.Amount));
        }
    }
}
```

### Finer-Grained Optimistic Concurrency in Multi-Stream Operations

When a handler uses multiple `[WriteAggregate]` parameters, Wolverine automatically applies version discovery only
to the **first** aggregate parameter. To opt a secondary stream into optimistic concurrency checking, use `VersionSource`:

```cs
public record TransferMoney(Guid FromId, Guid ToId, decimal Amount,
    long FromVersion, long ToVersion);

public static class TransferMoneyHandler
{
    public static void Handle(
        TransferMoney command,

        [WriteAggregate(nameof(TransferMoney.FromId),
            VersionSource = nameof(TransferMoney.FromVersion))]
        IEventStream<Account> fromAccount,

        [WriteAggregate(nameof(TransferMoney.ToId),
            VersionSource = nameof(TransferMoney.ToVersion))]
        IEventStream<Account> toAccount)
    {
        if (fromAccount.Aggregate.Amount >= command.Amount)
        {
            fromAccount.AppendOne(new Withdrawn(command.Amount));
            toAccount.AppendOne(new Debited(command.Amount));
        }
    }
}
```

## Enforcing Consistency Without New Events

The `AlwaysEnforceConsistency` option tells Polecat to perform an optimistic concurrency check on the stream even if no events
are appended:

```cs
[AggregateHandler(AlwaysEnforceConsistency = true)]
public static class MyAggregateHandler
{
    public static void Handle(DoSomething command, IEventStream<MyAggregate> stream)
    {
        // Even if no events are appended, Polecat will verify
        // the stream version hasn't changed since it was fetched
    }
}
```

For convenience, there is a `[ConsistentAggregateHandler]` attribute that automatically sets `AlwaysEnforceConsistency = true`.

### Parameter-level usage with `[ConsistentAggregate]`

```cs
public static class MyHandler
{
    public static void Handle(DoSomething command,
        [ConsistentAggregate] IEventStream<MyAggregate> stream)
    {
        // AlwaysEnforceConsistency is automatically true
    }
}
```

## Overriding Version Discovery

By default, Wolverine discovers a version member on your command type by looking for a property or field named `Version`
of type `int` or `long`. The `VersionSource` property lets you explicitly specify which member supplies the expected stream version:

```cs
public record TransferMoney(Guid FromId, Guid ToId, decimal Amount, long FromVersion);

[AggregateHandler(VersionSource = nameof(TransferMoney.FromVersion))]
public static class TransferMoneyHandler
{
    public static IEnumerable<object> Handle(TransferMoney command, Account account)
    {
        yield return new Withdrawn(command.Amount);
    }
}
```

For HTTP endpoints, `VersionSource` can resolve from route arguments, query string parameters, or request body members:

```cs
[WolverinePost("/orders/{orderId}/ship/{expectedVersion}")]
[EmptyResponse]
public static OrderShipped Ship(
    ShipOrder command,
    [Aggregate(VersionSource = "expectedVersion")] Order order)
{
    return new OrderShipped();
}
```

## Strong Typed Identifiers

You can use strong typed identifiers from tools like [Vogen](https://github.com/SteveDunn/Vogen) and [StronglyTypedId](https://github.com/andrewlock/StronglyTypedId)
within the "Aggregate Handler Workflow." You can also use hand rolled value types that wrap either `Guid` or `string`
as long as they conform to Polecat's rules about value type identifiers.

```cs
public record IncrementStrongA(LetterId Id);

public static class StrongLetterHandler
{
    public static AEvent Handle(IncrementStrongA command, [WriteAggregate] StrongLetterAggregate aggregate)
    {
        return new();
    }
}
```

## Natural Keys

Polecat supports [natural keys](/events/natural-keys) on aggregates, allowing you to look up event streams by a domain-meaningful identifier (like an order number) instead of the internal stream id. Wolverine's aggregate handler workflow fully supports natural keys, letting you route commands to the correct aggregate using a business identifier.

### Defining the Aggregate with a Natural Key

First, define your aggregate with a `[NaturalKey]` property and mark the methods that set the key with `[NaturalKeySource]`:

<!-- snippet: sample_wolverine_polecat_natural_key_aggregate -->
<!-- endSnippet -->

### Using Natural Keys in Command Handlers

When your command carries the natural key value instead of a stream id, Wolverine can resolve it automatically. The command property should match the aggregate's natural key type:

<!-- snippet: sample_wolverine_polecat_natural_key_commands -->
<!-- endSnippet -->

Wolverine uses the natural key type on the command property to call `FetchForWriting<TAggregate, TNaturalKey>()` under the covers, resolving the stream by the natural key in a single database round-trip.

### Handler Examples

Here are the handlers that process those commands, using `[WriteAggregate]` and `IEventStream<T>`:

<!-- snippet: sample_wolverine_polecat_natural_key_handlers -->
<!-- endSnippet -->

For more details on how natural keys work at the Polecat level, see the [Polecat natural keys documentation](/events/natural-keys).
