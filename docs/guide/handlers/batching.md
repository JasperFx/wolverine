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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Acceptance/batch_processing.cs#L160-L164' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_batch_processing_item' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Acceptance/batch_processing.cs#L166-L177' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_batch_processing_handler' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Acceptance/batch_processing.cs#L19-L47' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_configuring_batch_processing' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Acceptance/batch_processing.cs#L97-L136' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_send_end_to_end_with_batch' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Alright, with all that being said, here's a few more facts about the batch messaging support:

1. There is absolutely no need to create a specific message handler for the `Item` message, and in fact, you should
   not do so
2. The message batching is able to group the message batches by tenant id *if* your Wolverine system uses multi-tenancy

## What about durable messaging ("inbox")?

The durable inbox behaves just a little bit differently for message batching. Wolverine will technically
"handle" the individual messages, but does not mark them as handled in the message store until a batch message
that refers to the original message is completely processed. 

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Acceptance/batch_processing.cs#L181-L192' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_subtask_completed_messages' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

To teach Wolverine how to batch up our `SubTaskCompleted` messages into our custom batch message, we need to supply our own implementation of Wolverine's built in `Wolverine.Runtime.Batching.IMessageBatcher`
type:

<!-- snippet: sample_IMessageBatcher -->
<a id='snippet-sample_IMessageBatcher'></a>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Wolverine/Runtime/Batching/IMessageBatcher.cs#L5-L26' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_IMessageBatcher' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

A custom implementation of that interface in this case would look like this:

<!-- snippet: sample_SubTaskCompletedBatcher -->
<a id='snippet-sample_SubTaskCompletedBatcher'></a>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Acceptance/batch_processing.cs#L194-L226' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_SubTaskCompletedBatcher' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

And of course, this doesn't work without a matching message handler for our custom message type:

<!-- snippet: sample_SubTaskCompletedBatchHandler -->
<a id='snippet-sample_SubTaskCompletedBatchHandler'></a>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Acceptance/batch_processing.cs#L241-L258' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_SubTaskCompletedBatchHandler' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Acceptance/batch_processing.cs#L141-L154' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_registering_a_custom_message_batcher' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->



