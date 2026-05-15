# Event Forwarding

::: tip
As of Wolverine 2.2, you can use `IEvent<T>` as the message type in a handler as part of the event forwarding when you
need to utilize Polecat metadata
:::

::: warning
The Wolverine team recommends against combining this functionality with **also** using events as either a handler response
or cascaded messages as the behavior can easily become confusing. Instead, prefer using custom types for handler responses or HTTP response bodies
instead of the raw event types when using the event forwarding.
:::

The "Event Forwarding" feature immediately pushes any event captured by Polecat through Wolverine's persistent
outbox where there is a known subscriber (either a local message handler or a known subscriber rule to that event type).
The "Event Forwarding" publishes the new events as soon as the containing transaction is successfully committed. This is
different from the [Event Subscriptions](./subscriptions) in that there is no ordering guarantee, and does require you to
use the Wolverine transactional middleware for Polecat.

::: tip
The strong recommendation is to use either subscriptions or event forwarding, but not both in the same application.
:::

To be clear, this will work for:

* Any event type where the Wolverine application has a message handler for either the event type itself, or `IEvent<T>` where `T` is the event type
* Any event type where there is a known message subscription for that event type or its wrapping `IEvent<T>` to an external transport

Timing wise, the "event forwarding" happens at the time of committing the transaction for the original message that spawned the
new events, and the resulting event messages go out as cascading messages only after the original transaction succeeds -- just
like any other outbox usage. **There is no guarantee about ordering in this case.**

To opt into this feature, chain the `AddPolecat().EventForwardingToWolverine()` call as
shown below:

```cs
builder.Services.AddPolecat(opts =>
    {
        opts.Connection(connectionString);
    })
    .IntegrateWithWolverine()
    .EventForwardingToWolverine();
```

This does need to be paired with Wolverine configuration to add
subscriptions to event types like so:

```cs
builder.Host.UseWolverine(opts =>
{
    opts.PublishMessage<ChartingFinished>()
        .ToLocalQueue("charting")
        .UseDurableInbox();

    opts.Policies.AutoApplyTransactions();
});
```

## Projection Side-Effect Messages <Badge type="tip" text="6.0" />

Polecat projections can publish Wolverine messages as a side effect of a projection update by overriding `RaiseSideEffects` and calling `slice.PublishMessage(...)`. By default Polecat ships with a `NulloMessageOutbox` that drops every published message — projections that don't need messaging pay zero overhead.

`IntegrateWithWolverine()` flips that default and registers Wolverine.Polecat's `PolecatToWolverineOutbox` as the active `IMessageOutbox`. After that's wired, every `slice.PublishMessage(...)` is buffered in a `MessageContext` outbox enlisted in the projection's SQL transaction, and the buffered messages flush via Wolverine's outgoing-message machinery once the projection's database changes commit durably.

See Polecat's [`docs/events/projections/side-effects.md`](https://github.com/JasperFx/polecat/blob/main/docs/events/projections/side-effects.md) for the projection-author side of the contract (the `RaiseSideEffects` override shape and the `slice.PublishMessage` surface).

## Overriding Side-Effect Message Metadata <Badge type="tip" text="6.0" />

When a Polecat projection publishes a Wolverine message from `RaiseSideEffects` via `slice.PublishMessage(...)`, the resulting Wolverine envelope is built by an internal `MessageContext` that has no inherent knowledge of the originating event's metadata. By default the outgoing message ends up with no correlation id, no causation id, and an envelope-level conversation id rooted at its own envelope id — which means the chain Event A → side-effect command → Event B does not naturally share a correlation id.

The metadata-aware overload of `PublishMessage` (JasperFx.Events 2.0+) lets the projection author override the per-message metadata that the side-effect command's envelope (and the Polecat session opened for its handler) will inherit:

```csharp
public class TodoCloserProjection : MultiStreamProjection<TodoTask, Guid>
{
    public override ValueTask RaiseSideEffects(IDocumentSession session, IEventSlice<TodoTask> slice)
    {
        if (slice.Snapshot is null) return ValueTask.CompletedTask;

        // Carry the originating event's correlation id (and optionally its
        // causation id) onto the command we're emitting, so the handler that
        // closes the task can match against it.
        var correlationId = slice.Events()
            .Select(e => e.CorrelationId)
            .FirstOrDefault(id => id is not null);

        slice.PublishMessage(
            new CloseTodoTask(slice.Snapshot.Id),
            new MessageMetadata(slice.TenantId)
            {
                CorrelationId = correlationId,
                CausationId = slice.Events().Last().Id.ToString()
            });

        return ValueTask.CompletedTask;
    }
}
```

What the override actually does inside Wolverine — identical mapping to the Marten bridge:

| `MessageMetadata` field | Effect on the outgoing envelope | Effect on the receiving handler |
|---|---|---|
| `TenantId` | `envelope.TenantId` (also drives transport routing for tenanted endpoints) | `IMessageContext.TenantId`, scoped `DbContext` / `IDocumentSession` tenant |
| `CorrelationId` | `envelope.CorrelationId` (passes through `MessageBus.TrackEnvelopeCorrelation` without being clobbered) | `IMessageContext.CorrelationId`, `session.CorrelationId` (Polecat) |
| `CausationId` | Stored as `envelope.Headers["causation-id"]` because Wolverine's native `Envelope.ConversationId` is `Guid`-typed and would lose information | `session.CausationId` (Polecat) — `OutboxedSessionFactory` reads the header in preference to the default `ConversationId.ToString()` chain |
| `Headers` | Each entry copied onto `envelope.Headers` (string-converted) | Available via `envelope.Headers` on the receiving side |

The metadata-less form (`slice.PublishMessage(message)`) is unchanged and remains the right call when you don't need per-message overrides.

### When you actually need this

The motivating case is a "todo-list" projection: Event A opens a task keyed by some correlation id, Event B (emitted by the handler of a side-effect command) closes the task with the same key. Without the metadata override, Event B carries a null correlation id and the close never matches.

The same shape applies to anywhere you want a chain of derived events/commands to share a business-meaningful identifier — distributed tracing keyed on `correlation-id`, idempotency keys, tenant-scoped audit threading, etc. Pick the metadata fields that match your business need; you don't have to set all of them.
