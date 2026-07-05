# Batch Message Processing <Badge type="tip" text="3.0" />

Sometimes you might want to process a stream of incoming messages in batches rather than one at a time. This might
be for performance reasons, or maybe there's some kind of business logic that makes more sense to calculate for batches,
or maybe you want a logical ["debounce"](https://medium.com/@jamischarles/what-is-debouncing-2505c0648ff1) in how your system responds to the incoming messages. 

::: info
The batching is supported both for messages published in process to local queues and from incoming messages from
external transports.
:::

Regardless, Wolverine has a mechanism to locally batch incoming messages and forward them to a batch handler. First,
let's say that you have a message type called `Item`:

<!-- snippet: sample_batch_processing_item -->
<a id='snippet-sample_batch_processing_item'></a>
```cs
public record Item(string Name);
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Acceptance/batch_processing.cs#L158-L161' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_batch_processing_item' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

And for whatever reason, we need to process these messages in batches. To do that, we first need to have 
a message handler for an array of `Item` like so:

<!-- snippet: sample_batch_processing_handler -->
<a id='snippet-sample_batch_processing_handler'></a>
```cs
public static class ItemHandler
{
    public static void Handle(Item[] items)
    {
        // Handle this just like a normal message handler,
        // just that the message type is Item[]
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Acceptance/batch_processing.cs#L163-L173' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_batch_processing_handler' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

::: warning
At this point, Wolverine **only** supports an array of the message type for the batched handler
:::

::: tip
Batch message handlers are just like any other message handler and have no special rules about their
capabilities
:::

With that in our system, now we need to tell Wolverine to group `Item` messages, and we do that with the following
syntax:

<!-- snippet: sample_configuring_batch_processing -->
<a id='snippet-sample_configuring_batch_processing'></a>
```cs
theHost = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.BatchMessagesOf<Item>(batching =>
        {
            // Really the maximum batch size
            batching.BatchSize = 500;
            
            // You can alternatively override the local queue
            // for the batch publishing. 
            batching.LocalExecutionQueueName = "items";

            // We can tell Wolverine to wait longer for incoming
            // messages before kicking out a batch if there
            // are fewer waiting messages than the maximum
            // batch size
            batching.TriggerTime = 1.Seconds();
            
        })
            
            // The object returned here is the local queue configuration that
            // will handle the batched messages. This may be useful for fine
            // tuning the behavior of the batch processing
            .Sequential();
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Acceptance/batch_processing.cs#L19-L46' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_configuring_batch_processing' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

And that's that! Just to bring this a little more into focus, here's an end to end test from the Wolverine
codebase:

<!-- snippet: sample_send_end_to_end_with_batch -->
<a id='snippet-sample_send_end_to_end_with_batch'></a>
```cs
[Fact]
public async Task send_end_to_end_with_batch()
{
    // Items to publish
    var item1 = new Item("one");
    var item2 = new Item("two");
    var item3 = new Item("three");
    var item4 = new Item("four");

    Func<IMessageContext, Task> publish = async c =>
    {
        // I'm publishing the 4 items in sequence
        await c.PublishAsync(item1);
        await c.PublishAsync(item2);
        await c.PublishAsync(item3);
        await c.PublishAsync(item4);
    };

    // This is the "act" part of the test
    var session = await theHost.TrackActivity()
        
        // Wolverine testing helper to "wait" until
        // the tracking receives a message of Item[]
        .WaitForMessageToBeReceivedAt<Item[]>(theHost)
        .ExecuteAndWaitAsync(publish);

    // The four Item messages should be processed as a single 
    // batch message
    var items = session.Executed.SingleMessage<Item[]>();

    items.Length.ShouldBe(4);
    items.ShouldContain(item1);
    items.ShouldContain(item2);
    items.ShouldContain(item3);
    items.ShouldContain(item4);
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Acceptance/batch_processing.cs#L97-L135' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_send_end_to_end_with_batch' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Alright, with all that being said, here's a few more facts about the batch messaging support:

1. There is absolutely no need to create a specific message handler for the `Item` message, and in fact, you should
   not do so -- *unless* you are running in `MultipleHandlerBehavior.Separated` mode and deliberately want both a
   per-message handler and a batched handler (see [Combining a direct handler with batching](#combining-a-direct-handler-with-batching) below)
2. The message batching is able to group the message batches by tenant id *if* your Wolverine system uses multi-tenancy

## Combining a direct handler with batching

By default Wolverine assumes the batch handler is the *only* consumer of the element type, so an incoming `Item`
is always routed straight to the batch. If you *also* declare a direct `Handle(Item)` handler alongside
`BatchMessagesOf<Item>()`, the direct handler wins and the batch is silently shadowed -- the batched handler never runs.

::: warning
Because that shadowing is easy to miss, Wolverine logs a loud **warning at startup** whenever a message type has
both a direct `Handle(T)` handler and a `BatchMessagesOf<T>()` batch handler under the default
`ClassicCombineIntoOneLogicalHandler` mode, naming both handlers and pointing you at `MultipleHandlerBehavior.Separated`.
If you would rather this configuration be a hard error, call `opts.AssertNoBatchHandlerConflicts()` and Wolverine will
throw at startup instead of warning.
:::

The one exception is `MultipleHandlerBehavior.Separated`. Under that mode Wolverine treats the per-message handler and
the batched handler as two independent consumers of `Item`, so **both** run for every `Item`:

```csharp
opts.MultipleHandlerBehavior = MultipleHandlerBehavior.Separated;
opts.BatchMessagesOf<Item>();

// Direct, per-message handler
public static class ItemAuditHandler
{
    public static void Handle(Item item) { /* runs once per message */ }
}

// Batched handler
public static class ItemHandler
{
    public static void Handle(Item[] items) { /* runs once per assembled batch */ }
}
```

To make this work, Wolverine moves the batch onto its own dedicated local queue (the element type's queue name with a
`-batch` suffix) so it no longer collides with the direct handler's queue, and fans every `Item` out to both queues.
This applies to messages published in-process *and* to `Item` messages arriving from an external transport listener.

### Multiple batched handlers

`MultipleHandlerBehavior.Separated` also lets you register **more than one** batched handler for the same element type --
for example one handler that publishes an integration event for the batch and another that archives it:

```csharp
opts.MultipleHandlerBehavior = MultipleHandlerBehavior.Separated;
opts.BatchMessagesOf<Item>();

public static class ItemPublisher
{
    public static void Handle(Item[] items) { /* publish an integration event */ }
}

public static class ItemArchiver
{
    public static void Handle(Item[] items) { /* archive the batch */ }
}
```

Under `Separated` mode each `Handle(Item[])` handler is given its own sticky queue, so Wolverine fans the assembled
batch out to every one of them and each runs independently. (Under the default `Classic` behavior the multiple
`Handle(Item[])` handlers are instead combined into a single logical handler that invokes each one in turn.)

## Durability and message settlement

::: warning
**A durable (persistent) listener is required for guaranteed delivery of batched processing.** With an
inline or buffered listener, the individual messages are settled (acknowledged / marked handled) *before*
the batch they belong to actually runs, so a process crash while a batch is still accumulating loses those
messages. If you cannot afford to lose messages, batch behind a [durable inbox](/guide/durability/).
:::

Batching accumulates incoming messages in memory before forwarding them to your batch handler as a single
`T[]` (or custom batch) message. The important question is *when the original member messages are settled*
with the inbox or the broker, because anything accumulated but not yet run is only as safe as that settlement
timing. This differs by [endpoint mode](/guide/messaging/listeners):

| Endpoint mode | When the member messages are settled | Crash while a batch is accumulating |
|---|---|---|
| **Durable** | *After* the batch succeeds — members are held `InBatch` and only marked handled in the message store once the batch message completes | Members are recovered from the inbox and reprocessed — **no loss** |
| **Inline** | The moment each message is absorbed into the batcher — *before* the batch runs | Accumulated members are already settled and are **lost** |
| **Buffered** | At receipt — *before* the batch runs | Accumulated members are already settled and are **lost** |

In other words, deferred settlement is exactly what makes batching loss-proof, and the durable inbox is the
*only* mode that provides it. This is a deliberate design choice: holding a broker lease open across an entire
accumulation window (with per-transport lock renewal) is inherently transport-specific, whereas the durable
inbox already solves the loss problem uniformly across every transport.

The mechanics: a batched member is flagged `Envelope.InBatch = true` when it enters the batcher. On a durable
listener, `DurableReceiver.CompleteAsync` early-returns for any `InBatch` envelope, so the member stays
un-settled in the message store; only when the assembled batch message completes are all of its members marked
handled together. On inline and buffered listeners there is no inbox to defer to, so the member is settled as
soon as the batching handler absorbs it.

### The lock window caveat (lock-based transports)

On transports that hand out a time-bounded lock or lease per message — Azure Service Bus peek-lock, Amazon SQS
visibility timeout, and similar — the **accumulation window plus the batch processing time must fit inside that
lock duration**. Wolverine's broker listeners do not renew these locks for messages that are sitting inside a
batch waiting to be flushed.

If your `TriggerTime` (or the time to fill a `BatchSize` worth of messages) approaches or exceeds the broker's
lock/visibility window, the broker will consider the lock expired and **redeliver** those messages while they
are still buffered. The symptoms are silent duplicate processing and a `DeliveryCount` that climbs toward the
dead-letter threshold, with nothing logged by Wolverine to explain it. Guidance:

- Keep `TriggerTime` comfortably below the transport's lock/visibility duration (leaving headroom for the batch
  handler's own execution time), **or**
- Use a durable listener, where members are pulled from the persistent inbox rather than held under a broker
  lock, so the accumulation window is no longer bounded by the broker lease.

## What about durable messaging ("inbox")?

The durable inbox behaves just a little bit differently for message batching. Wolverine will technically
"handle" the individual messages, but does not mark them as handled in the message store until a batch message
that refers to the original message is completely processed. See [Durability and message
settlement](#durability-and-message-settlement) above for the full settlement model and why a durable listener
is required for guaranteed delivery.

## De-duplicating a batch with `CoalesceBy`

A very common batching workload is a "trigger storm": a bulk operation fires hundreds or thousands of
"recalculate" messages that actually concern only a few dozen distinct entities. Batching already collapses
those into one handler invocation, but the handler still sees every duplicate and recomputes the same entity
many times. `CoalesceBy` de-duplicates the batch by a key so the handler sees **one message per distinct key,
last message wins**:

```csharp
opts.BatchMessagesOf<RecalculateScores>(batching =>
{
    batching.BatchSize = 500;
    batching.TriggerTime = 10.Seconds();

    // The handler sees at most one RecalculateScores per AggregateId (the latest one)
    batching.CoalesceBy((RecalculateScores x) => x.AggregateId);
});
```

::: tip
The key selector's lambda parameter must be **explicitly typed** to the batched element type (e.g.
`(RecalculateScores x) => ...`) so both the message and key type arguments can be inferred.
:::

`CoalesceBy` is just sugar over the [`IMessageBatcher`](#custom-batching-strategies) seam — it installs a
built-in `CoalescingMessageBatcher<T, TKey>` instead of the default batcher. Like the default it first groups
by tenant id, then de-duplicates within each tenant group.

Crucially, coalescing only changes **what the handler sees** — never what gets acknowledged. Every original
member message still rides on the batch, so the transactional inbox/outbox tracking and dead-lettering behave
exactly as they do for a normal (non-coalesced) batch. If you drop from 1,000 messages to 40 distinct keys,
the handler runs once over 40 items, but all 1,000 member messages are settled with that batch.

## Batch identity with `IBatchContext`

A batched handler can inject `IBatchContext` to get read-only information about the batch it is processing —
useful for correlating log entries, emitting batch-level metrics, or making per-batch decisions:

```csharp
public static void Handle(Item[] items, IBatchContext batch)
{
    // batch.BatchId is a stable id for this assembled batch (correlate your logs with it)
    // batch.Members describes each original member message: MessageId, Attempts, SentAt
    logger.LogInformation("Processing batch {BatchId} of {Count} members", batch.BatchId, batch.Members.Count);
}
```

`IBatchContext` is purely informational; reading it never changes what is acknowledged or how the batch is
settled. When combined with `CoalesceBy`, `Members` still lists **every** original member message (all the
ones that settle with the batch), even though the `items` array the handler sees was de-duplicated.

## Isolating poison items with `ApplyItemException`

By default a failed batch is retried and dead-lettered **as a unit** — one poison message takes every other
message in the batch to the dead-letter queue with it. When your batch handler already knows *which* item is
bad (a validation failure, a specific row a bulk API rejected), it can throw `ApplyItemException` to isolate
just that item instead. Wolverine dead-letters only the named item(s) and dispositions the survivors, so the
healthy messages are not collateral damage:

```csharp
public static void Handle(Order[] orders)
{
    foreach (var order in orders)
    {
        if (!IsValid(order))
        {
            // Dead-letter this one order, re-run the batch handler over the rest
            throw ApplyItemException.DeadLetterAndReplayOthers(order);
        }
        // ... process order ...
    }
}
```

The static factories make the intent explicit at the call site:

```csharp
throw ApplyItemException.DeadLetterAndReplayOthers(badOrder);            // DLQ it, re-run the rest
throw ApplyItemException.DeadLetterAndAckOthers(bad1, bad2);             // DLQ them, ack the rest as-is
throw ApplyItemException.DeadLetter(poison: bads, ackItems: committed);  // DLQ bads, ack what I committed, replay the remainder
```

- **`DeadLetterAndReplayOthers`** — dead-letter the poison item(s), then re-run the batch handler over the
  remaining items as a fresh, reduced batch. Use it when the handler had not yet committed anything.
- **`DeadLetterAndAckOthers`** — dead-letter the poison item(s) and acknowledge every other item as-is (no
  re-run). Use it when the handler already committed the good items in the same transaction.
- **`DeadLetter(poison, ackItems)`** — dead-letter the poison item(s), acknowledge the items you explicitly
  committed, and replay the remainder.

Throwing the exception *is* the opt-in — there is no configuration to enable. The items are matched back to
their original messages by reference identity, so throw the factory with the actual object(s) handed to your
handler.

`ApplyItemException` is for failures the handler can *name*. For **opaque** failures — where the handler
throws but cannot tell which item was the culprit — use the `IsolateBatchMembers()` error policy below.

## Isolating an opaque batch failure with `IsolateBatchMembers`

When a batch handler throws an exception it can't attribute to a specific item, you can still avoid
dead-lettering the whole batch by isolating the failing member. The `IsolateBatchMembers()` error policy,
keyed on an exception type, re-runs each member of the failed batch as its own size-1 batch — so only the
member that actually reproduces the failure is dead-lettered, and every healthy member succeeds:

```csharp
// A deterministic error isolates the offending member...
opts.Policies.OnException<ValidationException>().IsolateBatchMembers();

// ...while a transient error still retries the whole batch (the two policies compose by exception type).
opts.Policies.OnException<SqlException>().RetryWithCooldown(100.Milliseconds(), 1.Seconds());
```

Because it is matched by exception type, it composes with the ordinary retry verbs: a transient
`SqlException` retries the whole batch with a cooldown, while a deterministic `ValidationException` isolates
the bad member. The isolation is bounded and one-time — a member that has already been reduced to a size-1
batch is simply dead-lettered rather than probed again. On a message type that is **not** batched,
`IsolateBatchMembers()` behaves like a plain move-to-error-queue (there is nothing to isolate).

### Probing after N whole-batch failures

When you don't have a specific exception type to key on — the batch just fails and you can't tell whether it's
transient or a poison item — configure a **count-based** probe directly on the batch. `ProbeIndividuallyAfter(N)`
retries the whole batch `N` times and *only then* falls back to isolating each member individually:

```csharp
opts.BatchMessagesOf<Order>(b =>
{
    b.BatchSize = 500;
    b.TriggerTime = 10.Seconds();

    // Retry the whole batch 3 times (in case the failure was transient); if it still fails, re-run each
    // member on its own so only the genuinely bad one dead-letters.
    b.ProbeIndividuallyAfter(attempts: 3);
});
```

This gives transient failures a chance to clear on retry before paying for the per-member probe, and like
`IsolateBatchMembers` it is bounded and one-time — once reduced to a size-1 batch, a failing member is
dead-lettered rather than probed again.

## Custom Batching Strategies

::: info
This feature was originally added for a [JasperFx Software](https://jasperfx.net) customer who needed to batch messages by a logical saga id.
:::

By default, Wolverine is simply batching messages of type `Item` into a message of type `Item[]`. But what if you need
to do something a little more custom? Like batching messages by a logical saga id or some kind of entity identity?

As an example, let's say that you are building some kind of task tracking system where you might easily have dozens of
sub tasks for each parent task that could be getting marked complete in rapid succession. That's maybe a good example 
of where batching would be handy. Let's say that we have two message types for the individual item message and a custom
task for the batched message like so:

<!-- snippet: sample_subtask_completed_messages -->
<a id='snippet-sample_subtask_completed_messages'></a>
```cs
// Messages at the granular level that might be streaming in
// very quickly
public record SubTaskCompleted(string TaskId, string SubTaskId);

// A custom message type for processing a batch of sub task
// completed messages. Note that it's batched by the TaskId
public record SubTaskCompletedBatch(string TaskId, string[] SubTaskIdList);
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Acceptance/batch_processing.cs#L177-L187' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_subtask_completed_messages' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

To teach Wolverine how to batch up our `SubTaskCompleted` messages into our custom batch message, we need to supply our own implementation of Wolverine's built in `Wolverine.Runtime.Batching.IMessageBatcher`
type:

<!-- snippet: sample_imessagebatcher -->
<a id='snippet-sample_imessagebatcher'></a>
```cs
/// <summary>
/// Plugin strategy for creating custom grouping of messages
/// </summary>
public interface IMessageBatcher
{
    /// <summary>
    /// Main method that batches items
    /// </summary>
    /// <param name="envelopes"></param>
    /// <returns></returns>
    IEnumerable<Envelope> Group(IReadOnlyList<Envelope> envelopes);
    
    /// <summary>
    /// The actual message type being built that is assumed to contain
    /// all the batched items
    /// </summary>
    Type BatchMessageType { get; }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Wolverine/Runtime/Batching/IMessageBatcher.cs#L5-L25' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_imessagebatcher' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

A custom implementation of that interface in this case would look like this:

<!-- snippet: sample_subtaskcompletedbatcher -->
<a id='snippet-sample_subtaskcompletedbatcher'></a>
```cs
public class SubTaskCompletedBatcher : IMessageBatcher
{
    public IEnumerable<Envelope> Group(IReadOnlyList<Envelope> envelopes)
    {
        var groups = envelopes
            // You can trust that the message will be a non-null SubTaskCompleted here
            .GroupBy(x => x.Message!.As<SubTaskCompleted>().TaskId)
            .ToArray();
        
        foreach (var group in groups)
        {
            var subTaskIdList = group
                .Select(x => x.Message)
                .OfType<SubTaskCompleted>()
                .Select(x => x.SubTaskId)
                .ToArray();
            
            var message = new SubTaskCompletedBatch(group.Key,
                subTaskIdList);

            // It's important here to pass along the group of envelopes that make up 
            // this batched message for Wolverine's transactional inbox/outbox
            // tracking
            yield return new Envelope(message, group);
        }
    }

    public Type BatchMessageType => typeof(SubTaskCompletedBatch);
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Acceptance/batch_processing.cs#L189-L220' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_subtaskcompletedbatcher' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

And of course, this doesn't work without a matching message handler for our custom message type:

<!-- snippet: sample_subtaskcompletedbatchhandler -->
<a id='snippet-sample_subtaskcompletedbatchhandler'></a>
```cs
public static class SubTaskCompletedBatchHandler
{
    public static Task<TrackedTask> LoadAsync(SubTaskCompletedBatch batch, ITrackedTaskRepository repository)
    {
        return repository.LoadAsync(batch.TaskId);
    }

    public static Task Handle(SubTaskCompletedBatch batch)
    {
        // actually do something here....

        return Task.CompletedTask;
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Acceptance/batch_processing.cs#L235-L251' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_subtaskcompletedbatchhandler' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

And finally, we need to tell Wolverine about the batching and the strategy for batching the `SubTaskCompleted`
message type:

<!-- snippet: sample_registering_a_custom_message_batcher -->
<a id='snippet-sample_registering_a_custom_message_batcher'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.BatchMessagesOf<SubTaskCompleted>(x =>
        {
            // We just have to let Wolverine know about our custom
            // message batcher
            x.Batcher = new SubTaskCompletedBatcher();
        });
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Acceptance/batch_processing.cs#L140-L152' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_registering_a_custom_message_batcher' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->



