# Event Forwarding

::: tip
As of Wolverine 2.2, you can use `IEvent<T>` as the message type in a handler as part of the event forwarding when you
need to utilize Marten metadata
:::

::: warning
The Wolverine team recommends against combining this functionality with **also** using events as either a handler response
or cascaded messages as the behavior can easily become confusing. Instead, prefer using custom types for handler responses or HTTP response bodies
instead of the raw event types when using the event forwarding.
:::

The "Event Forwarding" feature immediately pushes any event captured by Marten through Wolverine's persistent
outbox where there is a known subscriber (either a local message handler or a known subscriber rule to that event type). 
The "Event Forwarding" publishes the new events as soon as the containing transaction is successfully committed. This is
different from the [Event Subscriptions](./subscriptions) in that there is no ordering guarantee, and does require you to 
use the Wolverine transactional middleware for Marten. 

::: tip
The strong recommendation is to use either subscriptions or event forwarding, but not both in the same application.
:::

To be clear, this will work for:

* Any event type where the Wolverine application has a message handler for either the event type itself, or `IEvent<T>` where `T` is the event type
* Any event type where there is a known message subscription for that event type or its wrapping `IEvent<T>` to an external transport

Timing wise, the "event forwarding" happens at the time of committing the transaction for the original message that spawned the
new events, and the resulting event messages go out as cascading messages only after the original transaction succeeds -- just
like any other outbox usage. **There is no guarantee about ordering in this case.** Instead, Wolverine is trying to have these
events processed as soon as possible.

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

        opts.Connection(connString!);

        // There will be more here later...

        opts.Projections
            .Add<AppointmentDurationProjection>(ProjectionLifecycle.Async);

        // OR ???

        // opts.Projections
        //     .Add<AppointmentDurationProjection>(ProjectionLifecycle.Inline);

        opts.Projections.Add<AppointmentProjection>(ProjectionLifecycle.Inline);
        opts.Projections
            .Snapshot<ProviderShift>(SnapshotLifecycle.Async);
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/CQRSWithMarten/TeleHealth.WebApi/Program.cs#L59-L94' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_opting_into_wolverine_event_publishing' title='Start of snippet'>anchor</a></sup>
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

    // Automatic usage of transactional middleware as
    // Wolverine recognizes that an HTTP endpoint or message handler
    // persists data
    opts.Policies.AutoApplyTransactions();
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/CQRSWithMarten/TeleHealth.WebApi/Program.cs#L18-L42' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_configuring_wolverine_event_subscriptions' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

This forwarding of events is using an outbox that can be awaited in your tests using this extension method:

<!-- snippet: sample_save_in_martend_and_wait_for_outgoing_messages -->
<a id='snippet-sample_save_in_martend_and_wait_for_outgoing_messages'></a>
```cs
public static Task<ITrackedSession> SaveInMartenAndWaitForOutgoingMessagesAsync(this IHost host, Action<IDocumentSession> action, int timeoutInMilliseconds = 5000)
{
    var factory = host.Services.GetRequiredService<OutboxedSessionFactory>();

    return host.ExecuteAndWaitAsync(async context =>
    {
        var session = factory.OpenSession(context);
        action(session);
        await session.SaveChangesAsync();

        // Shouldn't be necessary, but real life says do it anyway
        await context.As<MessageContext>().FlushOutgoingMessagesAsync();
    }, timeoutInMilliseconds);
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/Wolverine.Marten/MartenTestingExtensions.cs#L33-L48' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_save_in_martend_and_wait_for_outgoing_messages' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

To be used in your tests such as this:

<!-- snippet: sample_execution_of_forwarded_events_can_be_awaited_from_tests -->
<a id='snippet-sample_execution_of_forwarded_events_can_be_awaited_from_tests'></a>
```cs
[Fact]
public async Task execution_of_forwarded_events_can_be_awaited_from_tests()
{
    var host = await Host.CreateDefaultBuilder()
        .UseWolverine()
        .ConfigureServices(services =>
        {
            services.AddMarten(Servers.PostgresConnectionString)
                .IntegrateWithWolverine().EventForwardingToWolverine(opts =>
                {
                    opts.SubscribeToEvent<SecondEvent>().TransformedTo(e =>
                        new SecondMessage(e.StreamId, e.Sequence));
                });
        }).StartAsync();

    var aggregateId = Guid.NewGuid();
    await host.SaveInMartenAndWaitForOutgoingMessagesAsync(session =>
    {
        session.Events.Append(aggregateId, new SecondEvent());
    }, 100_000);

    using var store = host.Services.GetRequiredService<IDocumentStore>();
    await using var session = store.LightweightSession();
    var events = await session.Events.FetchStreamAsync(aggregateId);
    events.Count.ShouldBe(2);
    events[0].Data.ShouldBeOfType<SecondEvent>();
    events[1].Data.ShouldBeOfType<FourthEvent>();
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/MartenTests/event_streaming.cs#L142-L171' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_execution_of_forwarded_events_can_be_awaited_from_tests' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Where the result contains `FourthEvent` because `SecondEvent` was forwarded as `SecondMessage` and that persisted `FourthEvent` in a handler such as:


<!-- snippet: sample_execution_of_forwarded_events_second_message_to_fourth_event -->
<a id='snippet-sample_execution_of_forwarded_events_second_message_to_fourth_event'></a>
```cs
public static Task HandleAsync(SecondMessage message, IDocumentSession session)
{
    session.Events.Append(message.AggregateId, new FourthEvent());
    return session.SaveChangesAsync();
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/MartenTests/event_streaming.cs#L219-L225' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_execution_of_forwarded_events_second_message_to_fourth_event' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Overriding Side-Effect Message Metadata <Badge type="tip" text="5.x" />

When a Marten projection publishes a Wolverine message from `RaiseSideEffects` via `slice.PublishMessage(...)`, the resulting Wolverine envelope is built by an internal `MessageContext` that has no inherent knowledge of the originating event's metadata. By default the outgoing message ends up with no correlation id, no causation id, and an envelope-level conversation id rooted at its own envelope id — which means the chain Event A → side-effect command → Event B does not naturally share a correlation id.

The metadata-aware overload of `PublishMessage` (JasperFx.Events 1.29+) lets the projection author override the per-message metadata that the side-effect command's envelope (and the Marten session opened for its handler) will inherit:

```csharp
public class TodoCloserProjection : MultiStreamProjection<TodoTask, Guid>
{
    public override ValueTask RaiseSideEffects(IDocumentOperations operations, IEventSlice<TodoTask> slice)
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

What the override actually does inside Wolverine:

| `MessageMetadata` field | Effect on the outgoing envelope | Effect on the receiving handler |
|---|---|---|
| `TenantId` | `envelope.TenantId` (also drives transport routing for tenanted endpoints) | `IMessageContext.TenantId`, scoped `DbContext`/`IDocumentSession` tenant |
| `CorrelationId` | `envelope.CorrelationId` (passes through `MessageBus.TrackEnvelopeCorrelation` without being clobbered) | `IMessageContext.CorrelationId`, `session.CorrelationId` (Marten) |
| `CausationId` | Stored as `envelope.Headers["causation-id"]` because Wolverine's native `Envelope.ConversationId` is `Guid`-typed and would lose information | `session.CausationId` (Marten) — `OutboxedSessionFactory` reads the header in preference to the default `ConversationId.ToString()` chain |
| `Headers` | Each entry copied onto `envelope.Headers` (string-converted) | Available via `envelope.Headers` on the receiving side |

The metadata-less form (`slice.PublishMessage(message)`) is unchanged and remains the right call when you don't need per-message overrides.

### When you actually need this

The motivating case is a "todo-list" projection: Event A opens a task keyed by some correlation id, Event B (emitted by the handler of a side-effect command) closes the task with the same key. Without the metadata override, Event B carries a null correlation id and the close never matches.

The same shape applies to anywhere you want a chain of derived events/commands to share a business-meaningful identifier — distributed tracing keyed on `correlation-id`, idempotency keys, tenant-scoped audit threading, etc. Pick the metadata fields that match your business need; you don't have to set all of them.
