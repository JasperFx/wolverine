# Process Manager via Handlers

You can build an event-sourced Process Manager with the Wolverine and Marten features that ship today. No new base class, no new package. This guide shows you how, with a worked sample you can clone.

The pattern is complementary to Wolverine's [Saga](/guide/durability/sagas) support, not a replacement. Saga stays the right tool when you want a single class per process, a framework-managed lifecycle, and simple document-backed state. The pattern described here trades that for event-sourced state, a full audit trail, and handlers you can test as pure functions.

## 1. Introduction

A Process Manager coordinates a long-running business operation that unfolds across multiple steps and multiple messages. Place order, confirm payment, reserve items, schedule shipment, handle a timeout if payment never arrives. Each step is triggered by a different message. Each step needs to see where the process is so it can decide what happens next.

Wolverine offers two first-class ways to carry that state.

**Saga** ([docs](/guide/durability/sagas), [Marten integration](/guide/durability/marten/sagas)) gives you a single stateful class that inherits from `Wolverine.Saga`. Marten persists the saga as a document. Handler methods live on the saga itself, mutate its fields, and eventually call `MarkCompleted()` to delete the document. One class equals one process. The lifecycle is framework-managed.

**Process Manager via handlers** (this guide) carries the process as an event stream instead of a document. You write a plain state type with `Apply` methods, and each message type gets its own handler that loads the stream via `FetchForWriting`, decides what to do, and appends events. There is no single class that "is" the process. The process is the stream, and the state type is a projection of it.

Pick Saga when:

- You want one class per process and the discoverability that comes with it.
- Simple coordination with timeouts is the primary concern.
- You do not need an audit trail of how the process arrived at its current state.
- `MarkCompleted()` and a framework-managed lifecycle matter to your team.

Pick Process Manager via handlers when:

- You want every state change on the process recorded as an event, replayable and queryable.
- You want to test each step as a pure function: `Handle(command, state)` returns events, no host, no database.
- The process state is part of the domain model, not just internal plumbing.
- You are already event-sourcing nearby aggregates and want the process to fit the same mental model.

Both options live happily side by side in the same application. The choice is per-process, not per-repository.

## 2. The Building Blocks

This pattern composes features you probably already know. What is new is the combination.

### `IEventStream<T>` and `FetchForWriting`

[`FetchForWriting<T>`](https://martendb.io/events/optimistic_concurrency.html) is the Marten entry point. It loads an event stream, replays its events through your aggregate type's `Apply` methods, and hands you back an `IEventStream<T>` whose `Aggregate` property is the projected state. You append new events to that stream; on `SaveChangesAsync`, Marten runs an optimistic concurrency check and persists them atomically.

```csharp
public interface IEventStream<out T> where T : notnull
{
    T? Aggregate { get; }
    long? StartingVersion { get; }
    long? CurrentVersion { get; }
    Guid Id { get; }
    string Key { get; }
    IReadOnlyList<IEvent> Events { get; }
    void AppendOne(object @event);
    void AppendMany(params object[] events);
}
```

`Aggregate` is null and `StartingVersion` is null when the stream does not exist yet. That is important and returns in [Section 3](#the-recipe) when you look at the start handler.

### `[AggregateHandler]`

`[AggregateHandler]` is a class-level attribute from `Wolverine.Marten` that wires the full `FetchForWriting` plus `SaveChangesAsync` plus concurrency-check middleware around every handler method on the class. You get four behaviors for free:

1. The stream id is extracted from the incoming message using convention-based resolution.
2. The stream is loaded and the aggregate projected through its `Apply` methods.
3. The handler receives the projected aggregate as a parameter.
4. Any events returned from the handler are appended to the stream, and the session is saved.

As a naming-based alternative, any static class whose name ends with `AggregateHandler` is treated as if it carried the attribute. This guide uses the explicit attribute form throughout for clarity.

### `[WriteAggregate]`

`[WriteAggregate]` is the parameter-level version of the same idea. It decorates a single handler parameter instead of the whole class. You reach for `[WriteAggregate]` when one of the following is true:

- You want to override the convention-based stream-id resolution on a per-handler basis, for example when an external integration event names the id differently: `[WriteAggregate("OrderId")] OrderFulfillmentState state`.
- You want only one handler method on a class to participate in the aggregate workflow, while the others do something else.
- You want to override concurrency style (`ConcurrencyStyle.Exclusive` for an advisory lock) for a specific handler.

If `[AggregateHandler]` works for your handler, prefer it. `[WriteAggregate]` is the escape hatch.

### `MartenOps.StartStream<T>`

`FetchForWriting` is how you attach handlers to an existing stream. To **create** the stream in the first place, you return a result built by `MartenOps.StartStream<TState>(id, events...)` from a plain handler. Wolverine recognizes the return value, writes the stream-start and the initial events in one transaction, and you are off to the races. The start handler has a different shape from continue handlers, and [Section 3](#the-recipe) is specific about why.

### `Apply` methods on the state type

The projected state is a plain C# class. For each event the process cares about, you write a method named `Apply(TheEvent e)` that mutates the instance. Marten's [single stream projection](https://martendb.io/events/projections/aggregate-projections.html) machinery finds these methods by convention, plays events through them, and hands back the reduced state. No base class, no interface, no framework type required. The only non-negotiable is a `public Guid Id { get; set; }` property, because Marten registers the type as a document type behind the scenes.

### `OutgoingMessages` for cascading work and timeouts

`OutgoingMessages` is a `List<object>` with intent. Return one from a handler and Wolverine dispatches every message in it through the outbox. The important part for a Process Manager is the scheduling overload:

```csharp
public class OutgoingMessages : List<object>, IWolverineReturnType
{
    void Delay<T>(T message, TimeSpan delay);
    void Schedule<T>(T message, DateTimeOffset time);
    void RespondToSender(object response);
    void ToEndpoint<T>(T message, string endpointName, DeliveryOptions? options);
}
```

`Delay` and `Schedule` are how you arm a payment timeout from the start handler without injecting `IMessageBus`. That keeps the handler testable as a pure function; the scheduled message is just an item in the returned list, and the test can assert on it directly.

### `Events` for fluent appending

`Events` (from the `Wolverine.Marten` namespace) is also a `List<object>`, and it is recognized by the aggregate-handler codegen as "append each of these to the stream." It exists so you can return multiple events from one handler without having to build a tuple:

```csharp
public static Events Handle(ConfirmPayment cmd, OrderFulfillmentState state)
{
    var events = new Events();
    events += new PaymentConfirmed(cmd.OrderFulfillmentStateId, cmd.Amount);
    if (state.ItemsReserved) events += new OrderFulfillmentCompleted(cmd.OrderFulfillmentStateId);
    return events;
}
```

For the common case of one event per handler, just return the event directly and Wolverine treats it as a single-item append. Use `Events` when the count is conditional. Use a tuple `(Events, OutgoingMessages)` when you want to append events **and** schedule follow-up messages in the same handler.

### How these pieces combine

Taken together, the ingredients look like this:

- A state type with `Apply` methods and a `Guid Id` property.
- A small catalogue of event records, past tense.
- A small catalogue of command records, imperative.
- One plain start handler that returns `IStartStream` via `MartenOps.StartStream<TState>`.
- One `[AggregateHandler]` class per continue message type. Each handler returns events, possibly an `Events` list, possibly an `(Events, OutgoingMessages)` tuple.
- A terminal event that every continue handler guards against, so late-arriving messages are no-ops instead of corruption.
- A Marten configuration that registers the state type as an inline snapshot projection.

[Section 3](#the-recipe) walks through each of those in order, using the order fulfillment sample as the worked example.

## 3. The Recipe

A Process Manager built with this pattern is a handful of small files, arranged in a predictable shape. The steps below map one-for-one to files in [the ProcessManagerSample](https://github.com/JasperFx/wolverine/tree/main/src/Samples/ProcessManagerSample). Open that alongside the recipe; it compiles, runs, and has 15 tests behind it.

### Step 1: Define the process state type

```csharp
public class OrderFulfillmentState
{
    public Guid Id { get; set; }

    public Guid CustomerId { get; set; }
    public decimal TotalAmount { get; set; }

    public bool PaymentConfirmed { get; set; }
    public bool ItemsReserved { get; set; }
    public bool ShipmentConfirmed { get; set; }

    public bool IsCompleted { get; set; }
    public bool IsCancelled { get; set; }

    public bool IsTerminal => IsCompleted || IsCancelled;

    public void Apply(OrderFulfillmentStarted e)
    {
        Id = e.OrderFulfillmentStateId;
        CustomerId = e.CustomerId;
        TotalAmount = e.TotalAmount;
    }

    public void Apply(PaymentConfirmed _) => PaymentConfirmed = true;
    public void Apply(ItemsReserved _) => ItemsReserved = true;
    public void Apply(ShipmentConfirmed _) => ShipmentConfirmed = true;
    public void Apply(OrderFulfillmentCompleted _) => IsCompleted = true;
    public void Apply(OrderFulfillmentCancelled _) => IsCancelled = true;
}
```

Three rules drive the shape.

A `public Guid Id { get; set; }` is non-negotiable. Marten registers this type as a document type when you snapshot it, and document types need a settable identity. Omit it and `CleanAllDataAsync` (which you will run in test setup) throws `InvalidDocumentException`.

Every event the process cares about gets an `Apply` method. Marten's aggregation machinery discovers them by convention. Keep them boring: parameter name of `_` is fine when all you need is the event's existence, not its payload.

An `IsTerminal` helper is not required by the framework, but every continue handler will check it, so computing it once on the state type avoids drift.

### Step 2: Define your events

```csharp
public record OrderFulfillmentStarted(
    Guid OrderFulfillmentStateId,
    Guid CustomerId,
    decimal TotalAmount);

public record PaymentConfirmed(Guid OrderFulfillmentStateId, decimal Amount);
public record ItemsReserved(Guid OrderFulfillmentStateId, Guid ReservationId);
public record ShipmentConfirmed(Guid OrderFulfillmentStateId, string TrackingNumber);

public record OrderFulfillmentCompleted(Guid OrderFulfillmentStateId);
public record OrderFulfillmentCancelled(Guid OrderFulfillmentStateId, string Reason);
```

Past tense. Records. Each carries the stream id as its first property.

Two rules make this easier than it looks. First, the id property is named `{AggregateTypeName}Id`, which is `OrderFulfillmentStateId` here. Wolverine's convention-based resolution finds it without any attribute. Second, the sample uses the same event type for both the incoming integration event and the stream event it records; the process "accepts" the external fact by writing it to its own stream. That is not the only valid modelling choice (you can keep them separate), but it is the tightest for a sample and mirrors how many real process managers work in practice.

Include **at least one terminal event**. The sample has two: a happy-path `OrderFulfillmentCompleted` and a compensating `OrderFulfillmentCancelled`. The compensating event will matter in Step 5 and again when you add a payment timeout in Step 6.

### Step 3: Define your commands

```csharp
public record StartOrderFulfillment(
    Guid OrderFulfillmentStateId,
    Guid CustomerId,
    decimal TotalAmount);

public record CancelOrderFulfillment(
    Guid OrderFulfillmentStateId,
    string Reason);
```

Imperative, records, and carrying the stream id on the same property name as the events. Keep commands and integration events in separate files so the reading order is clear (imperatives in one, facts in the other). The sample uses `Commands.cs` and `Events.cs`.

If the messages that trigger your process come from an external bounded context and already have their own id property name (for example, `OrderId` rather than `OrderFulfillmentStateId`), do not rename them. Use the `[WriteAggregate("OrderId")]` escape hatch from Step 4 instead.

### Step 4: Write your handlers

The start handler and the continue handlers are **different shapes**. Treating them uniformly is the most common early mistake with this pattern. The two shapes are covered in Steps 4a and 4b.

#### Step 4a: The start handler

```csharp
public static class StartOrderFulfillmentHandler
{
    public static IStartStream Handle(StartOrderFulfillment command)
    {
        var started = new OrderFulfillmentStarted(
            command.OrderFulfillmentStateId,
            command.CustomerId,
            command.TotalAmount);

        return MartenOps.StartStream<OrderFulfillmentState>(
            command.OrderFulfillmentStateId, started);
    }
}
```

A plain static class. No `[AggregateHandler]` attribute. The handler returns an `IStartStream` built by `MartenOps.StartStream<TState>(id, events...)`, and Wolverine takes care of creating the stream, appending the initial events, and calling `SaveChangesAsync`.

The reason this is not an `[AggregateHandler]`: `AggregateHandlerAttribute` defaults `OnMissing` to `OnMissing.Simple404`. When you apply it to a handler whose aggregate does not yet exist, the middleware short-circuits before your method runs. No events are appended, no exception is thrown, and the failure is silent. `MartenOps.StartStream` is the idiomatic way to express "this command creates the stream" and it matches what the Wolverine test suite does in `src/Persistence/MartenTests/AggregateHandlerWorkflow/`.

One corollary: a duplicated `StartOrderFulfillment` for the same id will fail, because Marten will reject the second stream-start. You can wrap the start handler in a "create if absent" check if your trigger source may deliver at least once, but most callers should guarantee a unique process id at dispatch time.

#### Step 4b: Continue handlers

```csharp
[AggregateHandler]
public static class PaymentConfirmedHandler
{
    public static Events Handle(PaymentConfirmed @event, OrderFulfillmentState state)
    {
        if (state.IsTerminal) return new Events();
        if (state.PaymentConfirmed) return new Events();

        var events = new Events();
        events += @event;

        if (state.ItemsReserved && state.ShipmentConfirmed)
        {
            events += new OrderFulfillmentCompleted(state.Id);
        }

        return events;
    }
}
```

One static class per trigger message. `[AggregateHandler]` at the class level wires `FetchForWriting` plus optimistic concurrency plus `SaveChangesAsync` around every handler method on the class. The method receives the projected `OrderFulfillmentState` already loaded from the stream.

Prefer `Events` (from the `Wolverine.Marten` namespace) as the return type. You get three benefits: appending a single event, appending two events when a step also trips completion, and returning an empty `Events` for no-op paths are all the same shape. Single-event returns work too, but they force you into nullable workarounds on the no-op paths, and nullable event returns are unsafe: the aggregate-handler code generator emits an unconditional `stream.AppendOne(variable)` with no null check, so a `return null;` will call `AppendOne(null)`.

If the incoming message's id property does not match the `{AggregateTypeName}Id` convention, use `[WriteAggregate("CustomName")] OrderFulfillmentState state` on the parameter and drop the class-level `[AggregateHandler]`.

### Step 5: Handle completion

Completion means two different things. Readers and reviewers conflate them. The sample keeps them as two separate guards at the top of every continue handler.

#### Step 5a: The terminal-state guard

```csharp
if (state.IsTerminal) return new Events();
```

This guard prevents a late-arriving integration event from corrupting a finished process. If the customer cancelled five minutes ago and the warehouse has not heard yet, an `ItemsReserved` message is going to arrive after `OrderFulfillmentCancelled`. Without the guard, you would append an `ItemsReserved` event to a cancelled stream and the projection would report `ItemsReserved == true` on a cancelled order. With the guard, the message is a silent no-op.

Every continue handler carries this line. For N continue handlers, that is N guard lines. There is no framework-level `MarkCompleted()`; discipline holds the invariant. This is one of the friction points in Section 5.

#### Step 5b: The step-level idempotency guard

```csharp
if (state.PaymentConfirmed) return new Events();
```

This guard prevents at-least-once redelivery of the same integration event from being recorded twice. Your transport will occasionally re-deliver the same `PaymentConfirmed` message. Without the guard, you would append two identical events. With it, the second delivery is a no-op.

The completion guard and the idempotency guard look similar but handle different failure modes. Keep them as two separate lines. Merging them would lose the distinction between "the process is closed" and "this specific fact is already recorded," and both happen in real systems.

#### The terminal event append

Any continue handler can be the one that trips completion. Whichever handler observes that the other two gates are already satisfied appends the terminal event along with its own fact:

```csharp
if (state.ItemsReserved && state.ShipmentConfirmed)
{
    events += new OrderFulfillmentCompleted(state.Id);
}
```

This keeps the terminal event in the hands of whichever step actually closes the process, rather than funnelling every step through a central "maybe complete" handler. The tradeoff is that the condition appears in every handler, with the two "other" flags named each time. For three steps this is fine; for a ten-step process this starts to ache.

The compensating handler is simpler:

```csharp
[AggregateHandler]
public static class CancelOrderFulfillmentHandler
{
    public static Events Handle(CancelOrderFulfillment command, OrderFulfillmentState state)
    {
        if (state.IsTerminal) return new Events();

        var events = new Events();
        events += new OrderFulfillmentCancelled(state.Id, command.Reason);
        return events;
    }
}
```

No step-level idempotency guard; cancellation is terminal by its first occurrence, so the terminal-state guard alone is enough.

### Step 6: Schedule timeouts

::: tip To be validated
The timeout path is implemented in Phase 5 of the sample. The API surface described here is verified from source; the compositional details (notably, whether `MartenOps.StartStream` composes cleanly with `OutgoingMessages` in a tuple return) are still to be confirmed in that phase, and this section will be revised with whatever is learned.
:::

You schedule a timeout without injecting `IMessageBus`. Return an `OutgoingMessages` alongside whatever the handler produces, and Wolverine dispatches each item through the outbox:

```csharp
public class OutgoingMessages : List<object>, IWolverineReturnType
{
    void Delay<T>(T message, TimeSpan delay);
    void Schedule<T>(T message, DateTimeOffset time);
    // ...
}
```

In the start handler:

```csharp
public static (IStartStream, OutgoingMessages) Handle(StartOrderFulfillment command)
{
    var started = new OrderFulfillmentStarted(
        command.OrderFulfillmentStateId,
        command.CustomerId,
        command.TotalAmount);

    var outgoing = new OutgoingMessages();
    outgoing.Delay(
        new PaymentTimeout(command.OrderFulfillmentStateId),
        TimeSpan.FromMinutes(15));

    return (
        MartenOps.StartStream<OrderFulfillmentState>(command.OrderFulfillmentStateId, started),
        outgoing);
}
```

The tuple return is Wolverine's multi-result convention. Event items go to the stream, `OutgoingMessages` items cascade, response items go back to the caller.

The `PaymentTimeout` handler itself is a continue handler like any other. Its completion guard makes it a no-op if payment confirmed before the timer fired, so you do not need to track or cancel the scheduled message explicitly: the process state decides whether the timeout does anything.

### Step 7: Wire it up

```csharp
builder.Services.AddMarten(opts =>
    {
        var connectionString = builder.Configuration.GetConnectionString("Marten");
        opts.Connection(connectionString!);
        opts.DatabaseSchemaName = "process_manager";

        opts.Projections.Snapshot<OrderFulfillmentState>(SnapshotLifecycle.Inline);
    })
    .IntegrateWithWolverine();

builder.Host.UseWolverine(opts =>
{
    opts.Policies.AutoApplyTransactions();
});
```

Two pieces worth highlighting.

`SnapshotLifecycle.Inline` is what makes the next `FetchForWriting` call see the previous one's effects without running an async daemon. If you already run projections in the background, you can keep them there for reads and still use inline for the process state; the two settings are independent.

`opts.Policies.AutoApplyTransactions()` ensures the Marten session is wrapped around every handler, which is what makes `SaveChangesAsync` actually run. Without it, the start handler's `IStartStream` return would not be persisted.

### Step 8: Test it

Two styles. Both belong in the test project, and both are cheap enough that you should write both.

**Unit test (pure function).** Construct the state directly, call the handler, assert on the returned events:

```csharp
[Fact]
public void payment_confirmed_also_completes_when_other_two_gates_are_already_satisfied()
{
    var state = new OrderFulfillmentState
    {
        Id = Guid.NewGuid(),
        ItemsReserved = true,
        ShipmentConfirmed = true
    };

    var result = PaymentConfirmedHandler.Handle(
        new PaymentConfirmed(state.Id, 249m), state);

    result.Count.ShouldBe(2);
    result[0].ShouldBeOfType<PaymentConfirmed>();
    result[1].ShouldBeOfType<OrderFulfillmentCompleted>();
}
```

No Wolverine host. No Marten. No async. The handler is a static method over plain inputs, and the state type has no base class, so constructing it by object initializer is trivial. This is one of the strongest arguments this pattern has over Saga.

**Integration test.** Use Alba plus Wolverine's tracking support to run a full `InvokeMessageAndWaitAsync` sequence:

```csharp
[Fact]
public async Task happy_path_ends_with_OrderFulfillmentCompleted()
{
    var id = Guid.NewGuid();

    await Host.InvokeMessageAndWaitAsync(new StartOrderFulfillment(id, Guid.NewGuid(), 249m));
    await Host.InvokeMessageAndWaitAsync(new PaymentConfirmed(id, 249m));
    await Host.InvokeMessageAndWaitAsync(new ItemsReserved(id, Guid.NewGuid()));
    await Host.InvokeMessageAndWaitAsync(new ShipmentConfirmed(id, "TRACK-ABC"));

    await using var session = Store.LightweightSession();
    var events = await session.Events.FetchStreamAsync(id);

    events.Count.ShouldBe(5);
    events[4].Data.ShouldBeOfType<OrderFulfillmentCompleted>();
}
```

`InvokeMessageAndWaitAsync` blocks until the transaction commits, so you can open a read session on the next line and see the appended events.

The sample project's [IntegrationContext.cs](https://github.com/JasperFx/wolverine/tree/main/src/Samples/ProcessManagerSample/ProcessManagerSample.Tests/IntegrationContext.cs) shows the Alba bootstrap used here. Two settings matter for test reliability: `services.MartenDaemonModeIsSolo()` and `services.RunWolverineInSoloMode()`. Without them you will fight the distributed durability machinery on every test run.

## 4. Worked Example

> _Pending Phase 7. Assembled from the actual `ProcessManagerSample` source at that point._

## 5. The Friction Points

> _Pending Phase 6. Written after the timeout path has been implemented._

## 6. When to Use Saga Instead

> _Pending Phase 6._

## 7. Optional: DCB Enhancement

> _Pending Phase 7._
