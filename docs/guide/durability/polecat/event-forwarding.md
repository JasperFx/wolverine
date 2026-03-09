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
