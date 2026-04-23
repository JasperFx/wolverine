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

> _Pending Phase 4. Written after the happy-path handlers exist in the sample._

## 4. Worked Example

> _Pending Phase 7. Assembled from the actual `ProcessManagerSample` source at that point._

## 5. The Friction Points

> _Pending Phase 6. Written after the timeout path has been implemented._

## 6. When to Use Saga Instead

> _Pending Phase 6._

## 7. Optional: DCB Enhancement

> _Pending Phase 7._
