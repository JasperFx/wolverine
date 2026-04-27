# Process Manager via Handlers

You can build an event-sourced Process Manager with the Wolverine and Marten features that ship today. No new base class, no new package. This guide shows you how, with a worked sample you can clone.

The pattern is complementary to Wolverine's [Saga](/guide/durability/sagas) support, not a replacement. Saga stays the right tool when you want a single class per process, a framework-managed lifecycle, and simple document-backed state. The pattern described here trades that for event-sourced state, a full audit trail, and handlers you can test as pure functions.

## 1. Introduction

A Process Manager coordinates a long-running business operation that unfolds across multiple steps and multiple messages. Place order, confirm payment, reserve items, schedule shipment, handle a timeout if payment never arrives. Each step is triggered by a different message. Each step needs to see where the process is so it can decide what happens next.

Wolverine's [Saga](/guide/durability/sagas) support (and its [Marten-backed integration](/guide/durability/marten/sagas)) is the first-class way to do this: a single stateful class, persisted as a document, with a framework-managed `MarkCompleted()` lifecycle. If you have not read those pages yet, read them first. This guide assumes you have.

This page describes a complementary pattern for the same class of problem: carrying the process as an **event stream** instead of a document, and coordinating the steps through ordinary `[AggregateHandler]` methods rather than a single Saga class. No new base class, no new package; every mechanism used here ships in the current versions of Wolverine and Marten.

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

`Delay` and `Schedule` are how you arm a payment timeout from the start handler without injecting `IMessageBus`. That keeps continue handlers testable as pure functions; the scheduled message is just an item in the returned list, and the test can assert on it directly. (The start handler itself is asymmetric, for reasons [Section 3 Step 4](#step-4-write-your-handlers) covers.)

### Returning events from handlers

Two return types work for multi-event appends from aggregate handlers.

`IEnumerable<object>` with `yield return` and `yield break` is the cleaner shape for most continue handlers:

```csharp
public static IEnumerable<object> Handle(PaymentConfirmed @event, OrderFulfillmentState state)
{
    if (state.IsTerminal) yield break;
    yield return @event;
    if (state.ItemsReserved && state.ShipmentConfirmed)
        yield return new OrderFulfillmentCompleted(state.Id);
}
```

`Events` (from `Wolverine.Marten`) is a `List<object>` with the same codegen recognition. It is the natural choice when returning a tuple alongside `OutgoingMessages`, since both legs of the tuple are explicit collections. For a single unconditional event, returning the event directly is also valid.

Avoid nullable single-event returns (`TEvent?`): the codegen emits `stream.AppendOne(variable)` with no null check, so `return null` calls `AppendOne(null)`. Use `IEnumerable<object>` with `yield break` or an empty `Events` for the no-op path.

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

A Process Manager built with this pattern is a handful of small files, arranged in a predictable shape. The steps below map one-for-one to files in [the ProcessManagerViaHandlers](https://github.com/JasperFx/wolverine/tree/main/src/Samples/ProcessManagerViaHandlers). Open that alongside the recipe; it compiles, runs, and has a full test suite behind it.

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

Simplified here for focus; [Step 6](#step-6-schedule-timeouts) shows the full shape with the scheduled payment timeout, which is what the actual sample file carries.

A plain static class. No `[AggregateHandler]` attribute. The handler returns an `IStartStream` built by `MartenOps.StartStream<TState>(id, events...)`, and Wolverine takes care of creating the stream, appending the initial events, and calling `SaveChangesAsync`.

The reason this is not an `[AggregateHandler]`: `AggregateHandlerAttribute` defaults `OnMissing` to `OnMissing.Simple404`. When you apply it to a handler whose aggregate does not yet exist, the middleware short-circuits before your method runs. No events are appended, no exception is thrown, and the failure is silent. `MartenOps.StartStream` is the idiomatic way to express "this command creates the stream" and it matches what the Wolverine test suite does in `src/Persistence/MartenTests/AggregateHandlerWorkflow/`.

One corollary: a duplicated `StartOrderFulfillment` for the same id fails with a `Marten.Exceptions.ExistingStreamIdCollisionException` ("Stream #{id} already exists in the database"). Wolverine propagates this exception through `InvokeMessageAndWaitAsync` with its original type intact; the transaction rolls back cleanly, so the first start's data remains authoritative. If your trigger source may deliver the start command at least once, catch this exception and convert it to an idempotent "already started, ignoring" response; otherwise let the caller guarantee a unique process id at dispatch time. The sample's [`starting_the_same_process_twice_throws_and_first_start_wins`](https://github.com/JasperFx/wolverine/tree/main/src/Samples/ProcessManagerViaHandlers/ProcessManagerViaHandlers.Tests/OrderFulfillment/when_starting_a_fulfillment.cs) test covers this exact scenario.

#### Step 4b: Continue handlers

```csharp
[AggregateHandler]
public static class PaymentConfirmedHandler
{
    public static IEnumerable<object> Handle(PaymentConfirmed @event, OrderFulfillmentState state)
    {
        if (state.IsTerminal) yield break;
        if (state.PaymentConfirmed) yield break;

        yield return @event;

        if (state.ItemsReserved && state.ShipmentConfirmed)
            yield return new OrderFulfillmentCompleted(state.Id);
    }
}
```

One static class per trigger message. `[AggregateHandler]` at the class level wires `FetchForWriting` plus optimistic concurrency plus `SaveChangesAsync` around every handler method on the class. The method receives the projected `OrderFulfillmentState` already loaded from the stream.

Return `IEnumerable<object>` and use `yield return` / `yield break`. The no-op early exits are `yield break`; events to append are `yield return`. See [Returning events from handlers](#returning-events-from-handlers) for the `Events` alternative and the single-event shorthand.

If the incoming message's id property does not match the `{AggregateTypeName}Id` convention, use `[WriteAggregate("CustomName")] OrderFulfillmentState state` on the parameter and drop the class-level `[AggregateHandler]`.

### Step 5: Handle completion

Completion means two different things. Readers and reviewers conflate them. The sample keeps them as two separate guards at the top of every continue handler.

#### Step 5a: The terminal-state guard

```csharp
if (state.IsTerminal) yield break;
```

This guard prevents a late-arriving integration event from corrupting a finished process. If the customer cancelled five minutes ago and the warehouse has not heard yet, an `ItemsReserved` message is going to arrive after `OrderFulfillmentCancelled`. Without the guard, you would append an `ItemsReserved` event to a cancelled stream and the projection would report `ItemsReserved == true` on a cancelled order. With the guard, the message is a silent no-op.

Every continue handler carries this line. For N continue handlers, that is N guard lines. There is no framework-level `MarkCompleted()`; discipline holds the invariant. This is one of the friction points in Section 5.

#### Step 5b: The step-level idempotency guard

```csharp
if (state.PaymentConfirmed) yield break;
```

This guard prevents at-least-once redelivery of the same integration event from being recorded twice. Your transport will occasionally re-deliver the same `PaymentConfirmed` message. Without the guard, you would append two identical events. With it, the second delivery is a no-op.

The completion guard and the idempotency guard look similar but handle different failure modes. Keep them as two separate lines. Merging them would lose the distinction between "the process is closed" and "this specific fact is already recorded," and both happen in real systems.

#### The terminal event append

Any continue handler can be the one that trips completion. Whichever handler observes that the other two gates are already satisfied appends the terminal event along with its own fact:

```csharp
if (state.ItemsReserved && state.ShipmentConfirmed)
    yield return new OrderFulfillmentCompleted(state.Id);
```

This keeps the terminal event in the hands of whichever step actually closes the process, rather than funnelling every step through a central "maybe complete" handler. The tradeoff is that the condition appears in every handler, with the two "other" flags named each time. For three steps this is fine; for a ten-step process this starts to ache.

The compensating handler is simpler:

```csharp
[AggregateHandler]
public static class CancelOrderFulfillmentHandler
{
    public static IEnumerable<object> Handle(CancelOrderFulfillment command, OrderFulfillmentState state)
    {
        if (state.IsTerminal) yield break;
        yield return new OrderFulfillmentCancelled(state.Id, command.Reason);
    }
}
```

No step-level idempotency guard; cancellation is terminal by its first occurrence, so the terminal-state guard alone is enough.

### Step 6: Schedule timeouts

You schedule a timeout without injecting `IMessageBus`. Return an `OutgoingMessages` alongside whatever the handler produces, and Wolverine dispatches each item through the outbox. The start handler returns a tuple. The first element creates the stream and appends the initial event; the second element schedules the timeout:

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
        command.PaymentTimeoutWindow ?? DefaultPaymentTimeoutWindow);

    return (
        MartenOps.StartStream<OrderFulfillmentState>(command.OrderFulfillmentStateId, started),
        outgoing);
}
```

The tuple return is Wolverine's multi-result convention. `IStartStream` is an `IMartenOp : ISideEffect`, so Wolverine's return-value unpacker applies it alongside the `OutgoingMessages` without either fighting the other. This is what keeps the start handler a single method instead of splitting stream creation and timeout scheduling into separate handlers.

#### Let state decide. Do not cancel the timer.

The timeout handler is a standard `[AggregateHandler]` that uses the same guards you wrote for every other continue handler:

```csharp
[AggregateHandler]
public static class PaymentTimeoutHandler
{
    public static IEnumerable<object> Handle(PaymentTimeout _, OrderFulfillmentState state)
    {
        if (state.IsTerminal) yield break;
        if (state.PaymentConfirmed) yield break;
        yield return new OrderFulfillmentCancelled(state.Id, "Payment timed out");
    }
}
```

Notice what is not here: there is no API to "cancel" the scheduled message when payment arrives early. You do not need one. When the timer fires, the handler loads the current state, sees that payment already confirmed, and yields nothing. The scheduled message becomes a silent no-op.

This is the cleanest ergonomic win this pattern has over a Saga plus explicit-cancel approach. A cancel-the-timer design has to race the cancel against the timer firing, needs a cancellation API, and breaks if the cancel message is lost. A let-state-decide design relies on the state being authoritative and always current, which is exactly what event sourcing gives you. The timeout handler stays pure. The start handler stays pure. No `IMessageBus` injection anywhere in the process.

One consequence worth flagging: a long timeout window leaves the scheduler holding a message the process no longer cares about. If your transport or durability store has per-message cost concerns at scale, you may want shorter windows or explicit cancellation anyway. The sample's 15-minute default is fine for most workloads.

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

**Testing scheduled messages.** `InvokeMessageAndWaitAsync` does not wait for a delayed message that the scheduler has yet to pick up. For timeout assertions you need a small polling helper with a generous deadline, because the scheduler fires "around" the requested delay plus one poll cycle:

```csharp
private async Task WaitForCondition(Guid id, Func<OrderFulfillmentState, bool> predicate)
{
    var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
    while (DateTime.UtcNow < deadline)
    {
        await using var session = Store.LightweightSession();
        var state = await session.Events.FetchLatest<OrderFulfillmentState>(id);
        if (state is not null && predicate(state)) return;

        await Task.Delay(TimeSpan.FromMilliseconds(250));
    }

    throw new TimeoutException($"Condition on state {id} not met within the observation window.");
}
```

Tests that rely on the scheduler firing then look like:

```csharp
await Host.InvokeMessageAndWaitAsync(new StartOrderFulfillment(
    id, Guid.NewGuid(), 10m,
    PaymentTimeoutWindow: TimeSpan.FromSeconds(1)));

await WaitForCondition(id, state => state.IsTerminal);
```

Keep the requested delay small in tests (1 to 2 seconds) and the observation window comfortably larger than one scheduler poll cycle. Do not use a bare `Task.Delay` as an observation window; you will get a flaky test that sometimes passes because the scheduler was fast and sometimes fails because it was slow.

The sample project's [IntegrationContext.cs](https://github.com/JasperFx/wolverine/tree/main/src/Samples/ProcessManagerViaHandlers/ProcessManagerViaHandlers.Tests/IntegrationContext.cs) shows the Alba bootstrap used here. Two settings matter for test reliability: `services.MartenDaemonModeIsSolo()` and `services.RunWolverineInSoloMode()`. Without them you will fight the distributed durability machinery on every test run.

## 4. Worked Example

This section is the reference: every file in the `OrderFulfillment` folder of [`ProcessManagerViaHandlers`](https://github.com/JasperFx/wolverine/tree/main/src/Samples/ProcessManagerViaHandlers), in the order you would read them, plus the Marten plus Wolverine wiring and one test of each style. The scenario is end-to-end order fulfillment with a payment timeout and a compensating cancellation path. 20 tests back the sample; two of them appear below.

### `OrderFulfillment/OrderFulfillmentState.cs`

```csharp
namespace ProcessManagerViaHandlers.OrderFulfillment;

/// <summary>
/// Event-sourced state for the order fulfillment process. Projected inline from the event stream
/// via Apply methods. Serves as the correlation surface for the handlers that coordinate payment,
/// warehouse, and shipping steps.
/// </summary>
public class OrderFulfillmentState
{
    // Required by Marten: FetchForWriting registers the aggregate type as a document type.
    // Without a public Guid Id { get; set; }, CleanAllDataAsync throws InvalidDocumentException.
    public Guid Id { get; set; }

    public Guid CustomerId { get; set; }
    public decimal TotalAmount { get; set; }

    public bool PaymentConfirmed { get; set; }
    public bool ItemsReserved { get; set; }
    public bool ShipmentConfirmed { get; set; }

    public bool IsCompleted { get; set; }
    public bool IsCancelled { get; set; }

    /// <summary>
    /// True once the process has reached a terminal state. Every continue handler must guard on this
    /// to stay idempotent against late-arriving messages after completion or cancellation.
    /// </summary>
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

### `OrderFulfillment/Events.cs`

```csharp
namespace ProcessManagerViaHandlers.OrderFulfillment;

public record OrderFulfillmentStarted(
    Guid OrderFulfillmentStateId,
    Guid CustomerId,
    decimal TotalAmount);

public record PaymentConfirmed(
    Guid OrderFulfillmentStateId,
    decimal Amount);

public record ItemsReserved(
    Guid OrderFulfillmentStateId,
    Guid ReservationId);

public record ShipmentConfirmed(
    Guid OrderFulfillmentStateId,
    string TrackingNumber);

public record OrderFulfillmentCompleted(Guid OrderFulfillmentStateId);

public record OrderFulfillmentCancelled(
    Guid OrderFulfillmentStateId,
    string Reason);
```

### `OrderFulfillment/Commands.cs`

```csharp
namespace ProcessManagerViaHandlers.OrderFulfillment;

public record StartOrderFulfillment(
    Guid OrderFulfillmentStateId,
    Guid CustomerId,
    decimal TotalAmount,
    TimeSpan? PaymentTimeoutWindow = null);

public record CancelOrderFulfillment(
    Guid OrderFulfillmentStateId,
    string Reason);

public record PaymentTimeout(Guid OrderFulfillmentStateId);
```

### `OrderFulfillment/Handlers/StartOrderFulfillmentHandler.cs`

```csharp
using Wolverine;
using Wolverine.Marten;

namespace ProcessManagerViaHandlers.OrderFulfillment.Handlers;

public static class StartOrderFulfillmentHandler
{
    public static readonly TimeSpan DefaultPaymentTimeoutWindow = TimeSpan.FromMinutes(15);

    public static (IStartStream, OutgoingMessages) Handle(StartOrderFulfillment command)
    {
        var started = new OrderFulfillmentStarted(
            command.OrderFulfillmentStateId,
            command.CustomerId,
            command.TotalAmount);

        var outgoing = new OutgoingMessages();
        outgoing.Delay(
            new PaymentTimeout(command.OrderFulfillmentStateId),
            command.PaymentTimeoutWindow ?? DefaultPaymentTimeoutWindow);

        return (
            MartenOps.StartStream<OrderFulfillmentState>(command.OrderFulfillmentStateId, started),
            outgoing);
    }
}
```

### `OrderFulfillment/Handlers/PaymentConfirmedHandler.cs`

```csharp
using Wolverine.Marten;

namespace ProcessManagerViaHandlers.OrderFulfillment.Handlers;

[AggregateHandler]
public static class PaymentConfirmedHandler
{
    public static IEnumerable<object> Handle(PaymentConfirmed @event, OrderFulfillmentState state)
    {
        if (state.IsTerminal) yield break;
        if (state.PaymentConfirmed) yield break;

        yield return @event;

        if (state.ItemsReserved && state.ShipmentConfirmed)
            yield return new OrderFulfillmentCompleted(state.Id);
    }
}
```

### `OrderFulfillment/Handlers/ItemsReservedHandler.cs`

```csharp
using Wolverine.Marten;

namespace ProcessManagerViaHandlers.OrderFulfillment.Handlers;

[AggregateHandler]
public static class ItemsReservedHandler
{
    public static IEnumerable<object> Handle(ItemsReserved @event, OrderFulfillmentState state)
    {
        if (state.IsTerminal) yield break;
        if (state.ItemsReserved) yield break;

        yield return @event;

        if (state.PaymentConfirmed && state.ShipmentConfirmed)
            yield return new OrderFulfillmentCompleted(state.Id);
    }
}
```

### `OrderFulfillment/Handlers/ShipmentConfirmedHandler.cs`

```csharp
using Wolverine.Marten;

namespace ProcessManagerViaHandlers.OrderFulfillment.Handlers;

[AggregateHandler]
public static class ShipmentConfirmedHandler
{
    public static IEnumerable<object> Handle(ShipmentConfirmed @event, OrderFulfillmentState state)
    {
        if (state.IsTerminal) yield break;
        if (state.ShipmentConfirmed) yield break;

        yield return @event;

        if (state.PaymentConfirmed && state.ItemsReserved)
            yield return new OrderFulfillmentCompleted(state.Id);
    }
}
```

### `OrderFulfillment/Handlers/CancelOrderFulfillmentHandler.cs`

```csharp
using Wolverine.Marten;

namespace ProcessManagerViaHandlers.OrderFulfillment.Handlers;

[AggregateHandler]
public static class CancelOrderFulfillmentHandler
{
    public static IEnumerable<object> Handle(CancelOrderFulfillment command, OrderFulfillmentState state)
    {
        if (state.IsTerminal) yield break;
        yield return new OrderFulfillmentCancelled(state.Id, command.Reason);
    }
}
```

### `OrderFulfillment/Handlers/PaymentTimeoutHandler.cs`

```csharp
using Wolverine.Marten;

namespace ProcessManagerViaHandlers.OrderFulfillment.Handlers;

[AggregateHandler]
public static class PaymentTimeoutHandler
{
    public static IEnumerable<object> Handle(PaymentTimeout _, OrderFulfillmentState state)
    {
        if (state.IsTerminal) yield break;
        if (state.PaymentConfirmed) yield break;
        yield return new OrderFulfillmentCancelled(state.Id, "Payment timed out");
    }
}
```

### `Program.cs` (Marten plus Wolverine wiring)

```csharp
using JasperFx;
using Marten;
using Marten.Events.Projections;
using ProcessManagerViaHandlers.OrderFulfillment;
using Wolverine;
using Wolverine.Marten;

var builder = WebApplication.CreateBuilder(args);

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

var app = builder.Build();

app.MapPost("/orders/start",
    (StartOrderFulfillment command, IMessageBus bus) => bus.InvokeAsync(command));

return await app.RunJasperFxCommands(args);

public partial class Program;
```

### Unit test (pure function)

From [`HandlerUnitTests.cs`](https://github.com/JasperFx/wolverine/tree/main/src/Samples/ProcessManagerViaHandlers/ProcessManagerViaHandlers.Tests/OrderFulfillment/HandlerUnitTests.cs):

```csharp
[Fact]
public void payment_confirmed_also_completes_when_other_two_gates_are_already_satisfied()
{
    var state = new OrderFulfillmentState
    {
        Id = Guid.NewGuid(),
        CustomerId = Guid.NewGuid(),
        TotalAmount = 100m,
        ItemsReserved = true,
        ShipmentConfirmed = true
    };
    var @event = new PaymentConfirmed(state.Id, state.TotalAmount);

    var result = PaymentConfirmedHandler.Handle(@event, state).ToList();

    result.Count.ShouldBe(2);
    result[0].ShouldBeOfType<PaymentConfirmed>();
    var completed = result[1].ShouldBeOfType<OrderFulfillmentCompleted>();
    completed.OrderFulfillmentStateId.ShouldBe(state.Id);
}
```

### Integration test (happy path, end to end)

From [`when_completing_a_fulfillment.cs`](https://github.com/JasperFx/wolverine/tree/main/src/Samples/ProcessManagerViaHandlers/ProcessManagerViaHandlers.Tests/OrderFulfillment/when_completing_a_fulfillment.cs):

```csharp
[Fact]
public async Task happy_path_ends_with_OrderFulfillmentCompleted()
{
    var id = Guid.NewGuid();

    await Host.InvokeMessageAndWaitAsync(new StartOrderFulfillment(id, Guid.NewGuid(), 249.00m));
    await Host.InvokeMessageAndWaitAsync(new PaymentConfirmed(id, 249.00m));
    await Host.InvokeMessageAndWaitAsync(new ItemsReserved(id, Guid.NewGuid()));
    await Host.InvokeMessageAndWaitAsync(new ShipmentConfirmed(id, "TRACK-ABC"));

    await using var session = Store.LightweightSession();
    var events = await session.Events.FetchStreamAsync(id);

    events.Count.ShouldBe(5);
    events[0].Data.ShouldBeOfType<OrderFulfillmentStarted>();
    events[1].Data.ShouldBeOfType<PaymentConfirmed>();
    events[2].Data.ShouldBeOfType<ItemsReserved>();
    events[3].Data.ShouldBeOfType<ShipmentConfirmed>();
    events[4].Data.ShouldBeOfType<OrderFulfillmentCompleted>();

    var state = await session.Events.FetchLatest<OrderFulfillmentState>(id);
    state.ShouldNotBeNull();
    state.IsCompleted.ShouldBeTrue();
    state.IsCancelled.ShouldBeFalse();
    state.PaymentConfirmed.ShouldBeTrue();
    state.ItemsReserved.ShouldBeTrue();
    state.ShipmentConfirmed.ShouldBeTrue();
}
```

The full integration and unit test suites cover the out-of-order, idempotency, cancellation, and timeout paths; they live in the sample's `OrderFulfillment/` test folder and are worth reading alongside this page.

## 5. The Friction Points

None of these are deal-breakers. They are the honest accounting of what is harder with this pattern than with a Saga. Read them before you commit, so the first one does not come as a surprise two weeks in.

### No single home for the process

A process with five trigger message types means five handler files. Nothing in the framework ties them together. If a reviewer asks "what runs on `PaymentConfirmed`," you grep. There is no `OrderFulfillmentProcess.cs` to open. The sample's `Handlers/` folder is a convention, not an enforcement; a future maintainer can add a sixth handler elsewhere and the discoverability drops further.

Saga gets this for free. One class per process, one file to read.

### Completion logic is distributed across handlers

The "maybe-complete" check lives in every continue handler, with the two "other" flags named each time:

```csharp
if (state.ItemsReserved && state.ShipmentConfirmed)
    yield return new OrderFulfillmentCompleted(state.Id);
```

For three steps this is fine. For ten steps, the condition appears ten times with nine-flag expressions, and the first maintainer who adds an eleventh step will miss at least one. You can factor the predicate onto the state type (`state.ReadyToCompleteAfter(typeof(PaymentConfirmed))`) to centralize it, but that is hand-written and not something the framework will nudge you toward.

Saga centralizes this in one `checkForCompletion()` method on the saga class.

### Every continue handler carries two guard lines

```csharp
if (state.IsTerminal) yield break;
if (state.PaymentConfirmed) yield break;
```

For N continue handlers, that is 2N guard lines. They are mechanical but they are not optional, and a missed guard produces data corruption (an `ItemsReserved` event appended to a cancelled stream) that your tests may not catch because the next read of state still looks "right."

Saga's `MarkCompleted()` plus framework-managed lifecycle means Wolverine itself short-circuits handlers on a completed saga. You write the `MarkCompleted()` call once; the framework enforces it everywhere.

### The start handler has a different shape from the continue handlers

Start: plain static class, returns `IStartStream` via `MartenOps.StartStream<T>`. Continue: `[AggregateHandler]` static class, returns `IEnumerable<object>`. Start has no `OrderFulfillmentState` parameter; continue handlers always do. The two shapes are small but they are different, and new readers will ask why.

This is a hard consequence of `AggregateHandlerAttribute.OnMissing` defaulting to `OnMissing.Simple404`. The attribute is designed around "the aggregate exists, load it, enforce concurrency." It does not naturally model "this command creates the aggregate." `MartenOps.StartStream` is the idiomatic workaround and it is fine once you know it, but you cannot hide the asymmetry from the reader.

One related trap worth flagging: the `ExistingStreamIdCollisionException` raised on a duplicate start inherits from `MartenException`, **not** from `ConcurrencyException`, so a Wolverine retry or error policy scoped to `ConcurrencyException` will not cover duplicate-start failures. Scope the policy to `MartenException` (or to `ExistingStreamIdCollisionException` specifically) if you want to catch them.

### Silent failure mode if you misapply `[AggregateHandler]` to a start handler

If you forget and put `[AggregateHandler]` on a start handler, the middleware short-circuits before your handler runs. No events are appended. No exception is thrown. The test "passes" the build and the handler signature, then fails your assertion on event count with no useful diagnostic. The first time you hit this, expect to spend an hour before you realize the handler body never ran.

### Nullable single-event returns are unsafe

Returning `TEvent?` from a continue handler is ergonomic for the "sometimes no event" case but the aggregate-handler codegen emits `stream.AppendOne(variable)` unconditionally with no null check. A `return null;` will call `AppendOne(null)`. Use `IEnumerable<object>` with `yield break` for the no-op path instead.

### Inline snapshot projection is a silent correctness dependency

The per-step idempotency guard (`if (state.PaymentConfirmed) yield break;`) depends on the inline projection having committed the previous step's effects before the next handler loads state. Register the projection as `SnapshotLifecycle.Inline` and this works. Forget, and duplicate deliveries will be double-written without any other test failure telling you why.

### No first-class test helper for "wait for scheduled message to fire"

`InvokeMessageAndWaitAsync` waits for the cascading work of a single dispatch. A delayed message held by the scheduler is not tracked by that call. The sample's timeout tests use a polling helper (`WaitForCondition` in the test project) which works fine but is extra code every sample project will reinvent. If you write several timeout tests, consider lifting the helper into a shared test utility.

## 6. When to Use Saga Instead

Saga is the right tool when any of the following hold:

- **You want one class per process.** Discoverability matters more to your team than the audit trail does. Open one file and see the whole state machine.
- **You do not need event history on the process itself.** A document showing "where the saga is now" is enough; a replayable log of "how it got here" would be dead weight.
- **Framework-managed completion matters.** You want to call `MarkCompleted()` in one place and have the framework stop dispatching to that instance. You do not want to maintain a completion guard on every handler.
- **Simple coordination with timeouts is the primary concern.** Kick off, run a few handlers, time out if one does not arrive. Saga has dedicated lifecycle support for this; the Process Manager via handlers recipe above is heavier for the same outcome.
- **Your team is already fluent with Saga.** Adding a second pattern for the same class of problems has a real cost in reviewability and onboarding. That cost is worth paying when the event-sourced benefits are load-bearing, not when they are merely nice-to-have.
- **The process state is not part of the domain model.** If the state is internal coordination plumbing rather than something the domain asks questions about ("show me the fulfillment history for order 1234"), there is no value in making it a first-class event stream.

Nothing stops you from mixing the two in the same application. Sagas for the short, internal coordination processes; Process Manager via handlers for the long-running, externally visible, auditable ones. The choice is per-process, not per-repository.

For the Saga-side mechanics see the [Saga documentation](/guide/durability/sagas) and the [Marten-backed Saga integration](/guide/durability/marten/sagas).

## 7. Optional: DCB Enhancement

Everything above keeps the Process Manager's decisions bounded by its own stream. Sometimes you need more. If a step has to take into account facts that live on **other** streams (a different aggregate's history, a cross-cutting event stream), a single-stream `[AggregateHandler]` is not enough. Marten's Dynamic Consistency Boundary (DCB) support, exposed in Wolverine as the `[BoundaryModel]` parameter attribute, is the right reach.

The shape is small and worth knowing even if you do not use it today:

```csharp
public static class BoundaryModelSubscribeStudentHandler
{
    public static EventTagQuery Load(BoundaryModelSubscribeStudentToCourse command)
        => EventTagQuery
            .For(command.CourseId)
            .AndEventsOfType<CourseCreated, CourseCapacityChanged,
                StudentSubscribedToCourse, StudentUnsubscribedFromCourse>()
            .Or(command.StudentId)
            .AndEventsOfType<StudentEnrolledInFaculty,
                StudentSubscribedToCourse, StudentUnsubscribedFromCourse>();

    public static StudentSubscribedToCourse Handle(
        BoundaryModelSubscribeStudentToCourse command,
        [BoundaryModel] SubscriptionState state)
    {
        // guard checks against state projected from events across both streams
        return new StudentSubscribedToCourse(...);
    }
}
```

The handler has two methods: `Load`, which returns an `EventTagQuery` describing the events (across one or more streams) that should feed the projection, and `Handle`, which receives the projected state built from those events. Marten's `FetchForWritingByTags<T>(query)` loads and projects; Wolverine's `[BoundaryModel]` parameter attribute wires the middleware.

Two sharp edges worth flagging up front:

- `EventTagQuery.For(tag)` without a following `AndEventsOfType<...>()` call produces zero conditions and throws at runtime. Always pair `For` with `AndEventsOfType`.
- `DcbConcurrencyException` does **not** inherit from `ConcurrencyException`; it inherits from `MartenException` directly. If you have retry policies configured for optimistic concurrency violations, they will not cover DCB violations. Add a separate `opts.OnException<DcbConcurrencyException>().RetryWithCooldown(...)` policy.

Rather than repeat a DCB sample here, we recommend reading the canonical reference that already exists in the Wolverine test suite: the University domain at [`src/Persistence/MartenTests/Dcb/University/`](https://github.com/JasperFx/wolverine/tree/main/src/Persistence/MartenTests/Dcb/University). It models student-to-course enrollment with cross-stream invariants (a student cannot subscribe to more than three courses, a course cannot exceed its capacity) and is the reference for `[BoundaryModel]` usage in the codebase.

DCB is still an evolving area. If your Process Manager lives entirely within its own stream, stay with the single-stream recipe above. Reach for DCB when the invariants you need to enforce span streams that are not yours to co-own.
