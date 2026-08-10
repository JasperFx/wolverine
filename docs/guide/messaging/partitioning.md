# Partitioned Sequential Messaging <Badge type="tip" text="5.0" />

::: tip
Concurrency can be hard, especially anytime there is any element of a system like
the storage for an entity or event stream or saga that is sensitive to simultaneous writes. I won't tell
you *not* to worry about this because you absolutely should be concerned with concurrency, but fortunately
Wolverine has [some helpful functionality to help you manage concurrency in your system](/tutorials/concurrency).
:::

"Partitioned Sequential Messaging" is a feature in Wolverine that tries to guarantee sequential processing
*within* groups of messages related to some sort of business domain entity within your system while also allowing
work to be processed in parallel for better throughput *between* groups of messages.

At this point, Wolverine supports this feature for:

1. Purely local processing within the current process
2. "Partitioning" the publishing of messages to external transports like Rabbit MQ or Amazon SQS over a range of queues where we have built
   specific support for this feature
3. "Partitioning" the processing of messages received from any external transport within a single process

## How It Works

Let's jump right to a concrete example. Let's say your building an order management system, so you're processing 
plenty of command messages against a single `Order`. You also expect -- or already know from testing or production issues 
-- that in normal operation you can expect your system to receive messages simultaneously that impact the same
`Order` and that when that happens your system either throws up from concurrent writes to the same entity or event stream 
or even worse, you possibly get incorrect or incomplete system state when changes from one command are overwritten by
changes from another command against the same `Order`.

With all of that being said, let's utilize Wolverine's "Partitioned Sequential Messaging" feature to alleviate the concurrent
access to any single `Order`, while hopefully allowing work against different `Order` entities to happily proceed in parallel.

First though, just to make this easy, let's make a little marker interface for our internal message types that will
make it easy for Wolverine to know which `Order` a given command relates to:

<!-- snippet: sample_order_commands_for_partitioning -->
<a id='snippet-sample_order_commands_for_partitioning'></a>
```cs
public interface IOrderCommand
{
    public string OrderId { get; }
}

public record ApproveOrder(string OrderId) : IOrderCommand;
public record CancelOrder(string OrderId) : IOrderCommand;
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/PartitioningSamples.cs#L199-L208' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_order_commands_for_partitioning' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

If we were only running our system on a single node so we only care about a single process, we can do this:

<!-- snippet: sample_opting_into_local_partitioned_routing -->
<a id='snippet-sample_opting_into_local_partitioned_routing'></a>
```cs
var builder = Host.CreateApplicationBuilder();
builder.UseWolverine(opts =>
{
    opts.MessagePartitioning
        // First, we're going to tell Wolverine how to determine the 
        // message group id 
        .ByMessage<IOrderCommand>(x => x.OrderId)

        // Next we're setting up a publishing rule to local queues 
        .PublishToPartitionedLocalMessaging("orders", 4, topology =>
        {
            topology.MessagesImplementing<IOrderCommand>();
            
            
            // this feature exists
            topology.MaxDegreeOfParallelism = PartitionSlots.Five;
            
            // Just showing you how to make additional Wolverine configuration
            // for all the local queues built from this usage
            topology.ConfigureQueues(queue =>
            {
                queue.TelemetryEnabled(true);
            });
        });
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/PartitioningSamples.cs#L44-L71' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_opting_into_local_partitioned_routing' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

So let's talk about what we set up in the code above. First, we've taught Wolverine how to determine the group
id of any message that implements the `IOrderCommand` interface. Next we've told Wolverine to publish any message
implementing our `IOrderCommand` interface to one of four [local queues](/guide/messaging/transports/local) named "orders1", "orders2", "orders3", and "orders4."
At runtime, when you publish an `IOrderCommand` within the system, Wolverine will determine the group id of the new message through the `IOrderCommand.OrderId` rule we created 
(it does get written to `Envelope.GroupId`). Once Wolverine has that `GroupId`, it needs to determine which of the "orders#"
queues to send the message, and the easiest way to explain this is really just to show the internal code:

<!-- snippet: sample_slotforsending -->
<a id='snippet-sample_slotforsending'></a>
```cs
/// <summary>
/// Uses a combination of message grouping id rules and a deterministic hash
/// to predictably assign envelopes to a slot to help "shard" message publishing.
/// </summary>
/// <param name="envelope"></param>
/// <param name="numberOfSlots"></param>
/// <param name="rules"></param>
/// <returns></returns>
public static int SlotForSending(this Envelope envelope, int numberOfSlots, MessagePartitioningRules rules)
{
    // This is where Wolverine determines the GroupId for the message
    // Note that you can also explicitly set the GroupId
    var groupId = rules.DetermineGroupId(envelope);
    
    // Pick one at random if we can't determine a group id, and has to be zero based
    if (groupId == null) return Random.Shared.Next(1, numberOfSlots) - 1;

    // Deterministically choose a slot based on the GroupId, but try
    // to more or less evenly distribute groups to the different
    // slots
    return Math.Abs(groupId.GetDeterministicHashCode() % numberOfSlots);
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Wolverine/Runtime/Partitioning/PartitionedMessagingExtensions.cs#L17-L41' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_slotforsending' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The code above manages publishing between the "orders1", "orders2", "orders3", and "orders4" queues. Inside of each of the 
local queues Wolverine is also using yet another round of grouped message segregation with a slightly different mechanism sorting 
mechanism to sort messages by their group id into separate, strictly ordered Channels. The `PartitionSlots` enum controls 
the number of parallel channels processing messages within a single listener. 

::: info
From our early testing, we quickly found out that the second level of partitioning within listeners only distributed messages
relatively evenly when you had an odd number of slots within the listener, so we opted for an enum to limit the values here rather than trying to assert
on invalid even numbers. 
:::

Then end result is that you do create some parallelism between message processing while guaranteeing that messages from
within a single group id will be executed sequentially.

In the end, you really need just 2-3 things:

1. Some way for Wolverine to determine the group id of a message, assuming you aren't explicitly passing that to Wolverine
2. Potentially a publishing rule for partitioned sending
3. Potentially a rule on each listening endpoint to use partitioned handling

## Inferred Grouping for Event Streams or Sagas

There are some built in message group id rules that you can opt into as shown below:

<!-- snippet: sample_inferred_message_group_id -->
<a id='snippet-sample_inferred_message_group_id'></a>
```cs
// Telling Wolverine how to assign a GroupId to a message, that we'll use
// to predictably sort into "slots" in the processing
opts.MessagePartitioning
        
    // This tells Wolverine to use the Saga identity as the group id for any message
    // that impacts a Saga or the stream id of any command that is part of the "aggregate handler workflow"
    // integration with Marten
    .UseInferredMessageGrouping()
    
    .PublishToPartitionedLocalMessaging("letters", 4, topology =>
    {
        topology.MessagesImplementing<ILetterMessage>();
        topology.MaxDegreeOfParallelism = PartitionSlots.Five;
        
        topology.ConfigureQueues(queue =>
        {
            queue.BufferedInMemory();
        });
    });
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/MartenTests/concurrency_resilient_sharded_processing.cs#L114-L135' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_inferred_message_group_id' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The built in rules *at this point* include:

* Using the saga identity of a message that is handled by a [Stateful Saga](/guide/durability/sagas)
* Using the stream/aggregate id of messages that are part of the [Aggregate Handler Workflow](/guide/durability/marten/event-sourcing) integration with Marten
* Using the `Order` property of messages that implement the `SequencedMessage` interface (used by [ResequencerSaga](/guide/durability/sagas#resequencer-saga)). Messages with a `null` order value receive a random group id so they are distributed independently

## Specifying Grouping Rules

Internally, Wolverine is using a list of implementations of this interface:

<!-- snippet: sample_igroupingrule -->
<a id='snippet-sample_igroupingrule'></a>
```cs
/// <summary>
/// Strategy for determining the GroupId of a message
/// </summary>
public interface IGroupingRule
{
    bool TryFindIdentity(Envelope envelope, out string groupId);
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Wolverine/Runtime/Partitioning/IGroupingRule.cs#L3-L12' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_igroupingrule' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Definitely note that these rules are fall through, and the order you declare the rules
are important. Also note that when you call into this syntax below it's combinatorial (just meaning that you
don't start over if you call into it multiple times):

<!-- snippet: sample_configuring_message_grouping_rules -->
<a id='snippet-sample_configuring_message_grouping_rules'></a>
```cs
var builder = Host.CreateApplicationBuilder();
builder.UseWolverine(opts =>
{
    opts.MessagePartitioning
        // Use saga identity or aggregate handler workflow identity
        // from messages as the group id
        .UseInferredMessageGrouping()

        // First, we're going to tell Wolverine how to determine the 
        // message group id for any message type that can be 
        // cast to this interface. Also works for concrete types too
        .ByMessage<IOrderCommand>(x => x.OrderId)

        // Use the Envelope.TenantId as the message group id
        // this could be valuable to partition work by tenant
        .ByTenantId()

        // Use a custom rule implementing IGroupingRULE with explicit code to determine
        // the group id
        .ByRule(new MySpecialGroupingRule());
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/PartitioningSamples.cs#L84-L107' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_configuring_message_grouping_rules' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Grouping by Property Name <Badge type="tip" text="5.17" />

If your message contracts are auto-generated (e.g. from `.proto` files) and you cannot add a marker interface,
you can use the `ByPropertyNamed()` rule to look for a property by name on any message type. This is a built-in
`IGroupingRule` that inspects the incoming message type at runtime for a property matching one of the specified names
and uses its value as the `GroupId`.

The first matching property name wins, and property values of any type are converted to `string` via `ToString()`.
Null property values result in `string.Empty`. If no matching property is found on a message type, the rule falls
through to the next rule in the chain.

The property accessor is compiled via `LambdaBuilder` and memoized per message type for performance.

<!-- snippet: sample_configuring_by_property_name -->
<a id='snippet-sample_configuring_by_property_name'></a>
```cs
var builder = Host.CreateApplicationBuilder();
builder.UseWolverine(opts =>
{
    opts.MessagePartitioning
        // Look for a property named "StreamId" or "Id" on the message type
        // and use its value as the GroupId for partitioned processing.
        // The first matching property name wins.
        // This is particularly useful when message types are auto-generated
        // (e.g. from .proto files) and cannot implement a marker interface.
        .ByPropertyNamed("StreamId", "Id");
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/PartitioningSamples.cs#L112-L125' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_configuring_by_property_name' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Explicit Group Ids

::: tip
Any explicitly specified group id will take precedence over the grouping rules in the previous section
:::

You can also explicitly specify a group id for a message when you send or publish it through
`IMessageBus` like this:

<!-- snippet: sample_send_message_with_group_id -->
<a id='snippet-sample_send_message_with_group_id'></a>
```cs
public static async Task SendMessageToGroup(IMessageBus bus)
{
    await bus.PublishAsync(
        new ApproveInvoice("AAA"),
        new() { GroupId = "agroup" });
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/PartitioningSamples.cs#L128-L136' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_send_message_with_group_id' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

If you are using [cascaded messages](/guide/handlers/cascading) from your message handlers, there's an extension method helper
just as a convenience like this:

<!-- snippet: sample_using_with_group_id_as_cascading_message -->
<a id='snippet-sample_using_with_group_id_as_cascading_message'></a>
```cs
public static IEnumerable<object> Handle(ApproveInvoice command)
{
    yield return new PayInvoice(command.Id).WithGroupId("aaa");
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/PartitioningSamples.cs#L190-L196' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_with_group_id_as_cascading_message' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Partitioned Publishing Locally

::: tip
You will also need to set up message grouping rules for the message partitioning to function
:::

If you need to use the partitioned sequential messaging just within a single process, the
`PublishToPartitionedLocalMessaging()` method shown below will set up both a publishing rule for multiple local queues and 
partitioned processing for those local queues. 

<!-- snippet: sample_opting_into_local_partitioned_routing -->
<a id='snippet-sample_opting_into_local_partitioned_routing'></a>
```cs
var builder = Host.CreateApplicationBuilder();
builder.UseWolverine(opts =>
{
    opts.MessagePartitioning
        // First, we're going to tell Wolverine how to determine the 
        // message group id 
        .ByMessage<IOrderCommand>(x => x.OrderId)

        // Next we're setting up a publishing rule to local queues 
        .PublishToPartitionedLocalMessaging("orders", 4, topology =>
        {
            topology.MessagesImplementing<IOrderCommand>();
            
            
            // this feature exists
            topology.MaxDegreeOfParallelism = PartitionSlots.Five;
            
            // Just showing you how to make additional Wolverine configuration
            // for all the local queues built from this usage
            topology.ConfigureQueues(queue =>
            {
                queue.TelemetryEnabled(true);
            });
        });
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/PartitioningSamples.cs#L44-L71' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_opting_into_local_partitioned_routing' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Partitioned Processing at any Endpoint

You can add partitioned processing to any listening endpoint like this:

<!-- snippet: sample_configuring_partitioned_processing_on_any_listener -->
<a id='snippet-sample_configuring_partitioned_processing_on_any_listener'></a>
```cs
var builder = Host.CreateApplicationBuilder();
builder.UseWolverine(opts =>
{
    opts.UseRabbitMq();

    // You still need rules for determining the message group id
    // of incoming messages!
    opts.MessagePartitioning
        .ByMessage<IOrderCommand>(x => x.OrderId);
    
    // We're going to listen
    opts.ListenToRabbitQueue("incoming")
        // To really keep our system from processing Order related
        // messages for the same order id concurrently, we'll
        // make it so that only one node actively processes messages
        // from this queue
        .ExclusiveNodeWithParallelism()

        // We're going to partition the message processing internally
        // based on the message group id while allowing up to 7 parallel
        // messages to be executed at once
        .PartitionProcessingByGroupId(PartitionSlots.Seven);
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/PartitioningSamples.cs#L14-L39' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_configuring_partitioned_processing_on_any_listener' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Exempting Message Types from Partitioned Processing <Badge type="tip" text="6.26" />

`PartitionProcessingByGroupId()` routes **every** message on the listener through a GroupId-keyed slot. That's
exactly right for the message types that need per-group ordering, but when one GroupId dominates the traffic, the
whole listener collapses toward sequential processing for *all* message types — including high-volume types
(metrics, telemetry, counters) that never needed any ordering at all.

You can exempt those message types from partitioned processing. Exempted types skip the GroupId slots entirely and
execute on the endpoint's normal parallel execution lane (its `MaxDegreeOfParallelism`), while every other message
type keeps its strict per-GroupId sequencing:

<!-- snippet: sample_exempting_message_types_from_partitioned_processing -->
<a id='snippet-sample_exempting_message_types_from_partitioned_processing'></a>
```cs
var builder = Host.CreateApplicationBuilder();
builder.UseWolverine(opts =>
{
    opts.UseRabbitMq();

    // Group all order-related messages by their order id...
    opts.MessagePartitioning
        .ByMessage<IOrderCommand>(x => x.OrderId)

        // ...but order telemetry needs no ordering guarantees at all, so
        // exempt it from partitioned processing. Exempted message types
        // execute at the endpoint's normal MaxDegreeOfParallelism instead
        // of being serialized behind a GroupId slot, so one dominant
        // GroupId can't collapse the whole listener to sequential
        // processing for messages that never asked for ordering.
        // There is also an overload taking a Func<Type, bool> filter.
        .ExemptFromPartitionedProcessing<OrderTelemetry>();

    opts.ListenToRabbitQueue("incoming")
        .PartitionProcessingByGroupId(PartitionSlots.Seven);
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/PartitioningSamples.cs#L140-L163' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_exempting_message_types_from_partitioned_processing' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The generic overload matches any message type that can be cast to the given type, so a marker interface or common
base class works. A `Func<Type, bool>` overload is available for predicate-based matching, and exemptions are
additive.

Two things to be aware of:

* This changes **only the in-process execution lane**. Durable inbox/outbox handling, message routing, sender-side
  queue sharding, and GroupId stamping are all unaffected — an exempted message on a durable endpoint is persisted
  to the inbox before execution exactly as before.
* An exempted message may execute concurrently with a non-exempted message carrying the same GroupId. Only exempt
  message types that truly require no ordering relative to the partitioned types.

## Partitioned Processing with Batched Handlers <Badge type="tip" text="6.25" />

::: warning
A [batched handler](/guide/handlers/batching) needs help to participate in partitioned sequential processing, and
without it the failure is silent. The default batcher groups only by tenant id, so the assembled batch envelope
carries no group id -- and as `SlotForProcessing` above shows, a missing group id means *a randomly chosen slot*,
not "leave this unpartitioned". Successive batches for the same entity land on different slots and run
concurrently.
:::

**When the batched message type belongs to a partitioned topology, this is handled for you.** If the element type
matches a `PublishToPartitionedLocalMessaging` or [`GlobalPartitioned`](#global-partitioning) topology, Wolverine
groups the batches by group id and runs each batch on that group's topology slot -- the same slot the unbatched
messages for that group are being sequenced onto, so the batched handler is one more writer in the same queue rather
than a concurrent one beside it:

```csharp
opts.MessagePartitioning
    .ByMessage<IOrderCommand>(x => x.OrderId)
    .PublishToPartitionedLocalMessaging("orders", 4, topology =>
    {
        topology.MessagesImplementing<IOrderCommand>();
    });

// OrderPlaced is an IOrderCommand, so its batches land on the "orders{n}" slot for
// each batch's OrderId -- sequenced against the unbatched IOrderCommand handlers
// for that same OrderId.
opts.BatchMessagesOf<OrderPlaced>(batching => batching.TriggerTime = 1.Seconds());
```

Setting `LocalExecutionQueueName`, or calling `ExecuteOnDedicatedLocalQueue()`, opts back out and runs the batches
on their own queue.

Outside a topology -- a plain listener with only `PartitionProcessingByGroupId` applied -- there is no queue to
place the batch on, because the unbatched handlers execute inside the listener's own execution block. The most you
can get there is sequencing the batches for a group id against *each other*, by stamping the group id and sharding
the batching queue:

```csharp
opts.BatchMessagesOf<OrderPlaced>(batching =>
{
    // Group by the message group id, and stamp it onto the batch envelope
    batching.GroupByGroupId();
})
    // The local queue that runs the batched handler is a listening endpoint
    // like any other
    .PartitionProcessingByGroupId(PartitionSlots.Five);
```

See [Batching inside a partitioned topology](/guide/handlers/batching#batching-inside-a-partitioned-topology) and
[GH-3867](https://github.com/JasperFx/wolverine/issues/3867) for the details.

## Partitioned Publishing to External Transports

::: info
Wolverine supports the Azure Service Bus concept of [session identifiers](/guide/messaging/transports/azureservicebus/session-identifiers) that effectively provides the same
benefits as this feature.
:::

::: tip
Even if your system is not messaging to any other systems, using this mechanism will help distribute work across an
application cluster while guaranteeing that messages within a group id are processed sequentially and still allowing for
parallelism between message groups.
:::

Wolverine has direct support for partitioned routing to all ten of the transports that support the
[global partitioning](#global-partitioning) topology through a `PublishToSharded*()` companion to each
`UseSharded*()` extension method:

| Transport | Extension Method |
|-----------|-----------------|
| RabbitMQ | `PublishToShardedRabbitQueues()` |
| Kafka | `PublishToShardedKafkaTopics()` |
| Amazon SQS | `PublishToShardedAmazonSqsQueues()` |
| Pulsar | `PublishToShardedPulsarTopics()` |
| Azure Service Bus | `PublishToShardedAzureServiceBusQueues()` |
| GCP Pub/Sub | `PublishToShardedPubsubTopics()` |
| NATS | `PublishToShardedNatsSubjects()` |
| Redis Streams | `PublishToShardedRedisStreams()` |
| PostgreSQL | `PublishToShardedPostgresqlQueues()` |
| Sql Server | `PublishToShardedSqlServerQueues()` |

Note that in both of the following examples, Wolverine is both setting up publishing rules out to these queues, and also configuring
listeners for the queues. Beyond that, Wolverine is making each queue be "exclusive," meaning that only one node
within a cluster is actively listening and processing messages from each partitioned queue at any one time.

For Rabbit MQ:

<!-- snippet: sample_defining_partitioned_routing_for_rabbitmq -->
<a id='snippet-sample_defining_partitioned_routing_for_rabbitmq'></a>
```cs
// opts is the WolverineOptions from within an Add/UseWolverine() call

// Telling Wolverine how to assign a GroupId to a message, that we'll use
// to predictably sort into "slots" in the processing
opts.MessagePartitioning.ByMessage<ILetterMessage>(x => x.Id.ToString());

// This is creating Rabbit MQ queues named "letters1" etc. 
opts.MessagePartitioning.PublishToShardedRabbitQueues("letters", 4, topology =>
{
    topology.MessagesImplementing<ILetterMessage>();
    topology.MaxDegreeOfParallelism = PartitionSlots.Five;
    
    topology.ConfigureSender(x =>
    {
        // just to show that you can do this...
        x.DeliverWithin(5.Minutes());
    });
    topology.ConfigureListening(x => x.BufferedInMemory());
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/RabbitMQ/Wolverine.RabbitMQ.Tests/concurrency_resilient_sharded_processing.cs#L69-L90' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_defining_partitioned_routing_for_rabbitmq' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

And for Amazon SQS:

<!-- snippet: sample_partitioned_publishing_through_amazon_sqs -->
<a id='snippet-sample_partitioned_publishing_through_amazon_sqs'></a>
```cs
// Telling Wolverine how to assign a GroupId to a message, that we'll use
// to predictably sort into "slots" in the processing
opts.MessagePartitioning.ByMessage<ILetterMessage>(x => x.Id.ToString());

opts.MessagePartitioning.PublishToShardedAmazonSqsQueues("letters", 4, topology =>
{
    topology.MessagesImplementing<ILetterMessage>();
    topology.MaxDegreeOfParallelism = PartitionSlots.Five;
    
    topology.ConfigureListening(x => x.BufferedInMemory().MessageBatchSize(10));

});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/AWS/Wolverine.AmazonSqs.Tests/concurrency_resilient_sharded_processing.cs#L71-L85' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_partitioned_publishing_through_amazon_sqs' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Propagating GroupId to PartitionKey <Badge type="tip" text="5.17" />

When using Kafka (or any transport that uses `PartitionKey`), you may want cascaded messages from a handler to
automatically inherit the originating message's `GroupId` as their `PartitionKey`. This ensures that cascaded messages
land on the same Kafka partition as the originating message without manually specifying `DeliveryOptions` on every
outgoing message.

This is especially useful when you have a chain of message handlers where the first message arrives at a Kafka topic
with a consumer group id, and you want all downstream cascaded messages to be published to the same partition.

<!-- snippet: sample_propagate_group_id_to_partition_key -->
<a id='snippet-sample_propagate_group_id_to_partition_key'></a>
```cs
var builder = Host.CreateApplicationBuilder();
builder.UseWolverine(opts =>
{
    // Automatically propagate the originating message's GroupId
    // to the PartitionKey of all cascaded outgoing messages.
    // This is particularly useful with Kafka where you want
    // cascaded messages to land on the same partition as the
    // originating message without manually specifying
    // DeliveryOptions on every outgoing message.
    opts.Policies.PropagateGroupIdToPartitionKey();
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/PartitioningSamples.cs#L168-L181' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_propagate_group_id_to_partition_key' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

::: tip
The rule will not override an explicitly set `PartitionKey` on an outgoing envelope. If you set `PartitionKey` via
`DeliveryOptions`, that value takes precedence.
:::

## Global Partitioning

Global partitioning extends the [partitioned publishing](#partitioned-publishing-to-external-transports) concept to support multi-node deployments where messages must be processed sequentially by group id across the entire cluster, not just within a single node.

### How It Works

When you configure global partitioning, Wolverine:

1. **Links local queues to external transport queues** -- Each external transport endpoint (e.g., a RabbitMQ queue or Kafka topic) gets a companion local queue. The external queue acts as the coordination point across nodes, while the local queue handles the actual sequential processing within a node.

2. **Smart routing based on listener ownership** -- When a message is published, Wolverine checks whether the current node owns the exclusive listener for the target shard. If it does, the message is routed directly to the companion local queue (avoiding unnecessary network hops). If the shard is owned by another node, the message is sent through the external transport so it reaches the correct node.

3. **Support for modular monoliths** -- You can configure multiple global partitioning topologies for the same message type in different modules. Each module can have its own set of sharded queues and routing rules, allowing independent sequential processing pipelines within a single application.

::: tip
In single-node mode, global partitioning automatically shortcuts all messages to the companion local queues since the current node owns all listeners.
:::

### Configuration

Global partitioning is configured through `MessagePartitioningRules.GlobalPartitioned()`. You need to:

1. Set up a message partitioning strategy (e.g., `ByMessage<T>()` or `UseInferredMessageGrouping()`)
2. Configure the external transport topology (sharded queues/topics)
3. Specify which message types participate in global partitioning

The external and local topologies are automatically created with matching shard counts. The local queues are named with a `global-` prefix followed by the base name (e.g., `global-orders1`, `global-orders2`, etc.).

### Transport-Specific Configuration

Each supported transport has its own extension method for configuring the external topology:

| Transport | Extension Method | Documentation |
|-----------|-----------------|---------------|
| RabbitMQ | `UseShardedRabbitQueues()` | [RabbitMQ Global Partitioning](/guide/messaging/transports/rabbitmq/#global-partitioning) |
| Kafka | `UseShardedKafkaTopics()` | [Kafka Global Partitioning](/guide/messaging/transports/kafka#global-partitioning) |
| Amazon SQS | `UseShardedAmazonSqsQueues()` | [SQS Global Partitioning](/guide/messaging/transports/sqs/#global-partitioning) |
| Pulsar | `UseShardedPulsarTopics()` | [Pulsar Global Partitioning](/guide/messaging/transports/pulsar#global-partitioning) |
| Azure Service Bus | `UseShardedAzureServiceBusQueues()` | [Azure Service Bus Global Partitioning](/guide/messaging/transports/azureservicebus/#global-partitioning) |
| GCP Pub/Sub | `UseShardedPubsubTopics()` | [GCP Pub/Sub Global Partitioning](/guide/messaging/transports/gcp-pubsub/#global-partitioning) |
| NATS | `UseShardedNatsSubjects()` | [NATS Global Partitioning](/guide/messaging/transports/nats#global-partitioning) |
| Redis Streams | `UseShardedRedisStreams()` | [Redis Global Partitioning](/guide/messaging/transports/redis#global-partitioning) |
| PostgreSQL | `UseShardedPostgresqlQueues()` | [PostgreSQL Global Partitioning](/guide/durability/postgresql#global-partitioning) |
| Sql Server | `UseShardedSqlServerQueues()` | [Sql Server Global Partitioning](/guide/durability/sqlserver#global-partitioning) |

All ten extension methods share the same signature, `(string baseName, int numberOfEndpoints)`, and create endpoints named `baseName1`, `baseName2`, and so on, with matching companion local queues. Swap the RabbitMQ call in the example below for any of the others to use a different transport, for example `topology.UseShardedAzureServiceBusQueues("sequenced", 5)` or `topology.UseShardedNatsSubjects("sequenced", 5)`.

A couple of transport-specific notes:

* **Kafka** -- all nodes listening to the sharded topics share a single Kafka consumer group named after the base name so that Kafka assigns each topic's partitions exclusively to one consumer at a time. Wolverine stamps that consumer group id onto the `GroupId` of incoming envelopes by default, which you can turn off per listener with `DisableConsumerGroupIdStamping()` when the consumer group name is not meaningful as envelope metadata (e.g. when combined with `PropagateGroupIdToPartitionKey()`).
* **Azure Service Bus** -- the broker's native [session identifiers](/guide/messaging/transports/azureservicebus/session-identifiers) provide strictly ordered, per-session processing with a single queue and may be a simpler alternative if you are exclusively on Azure Service Bus. Global partitioning is the transport-agnostic option that behaves the same way across every broker in the table above.
* **PostgreSQL / Sql Server** -- the database queues need no extra infrastructure at all; each shard is just another pair of tables in the database you already have. They are inherently durable, which suits global partitioning since the topology forces `EndpointMode.Durable` on every slot anyway. The Sql Server shard queues additionally opt into the [`seq`-clustered high-throughput table layout](/guide/durability/sqlserver#optimizing-queue-throughput) by default.

### Example with RabbitMQ

<!-- snippet: sample_global_partitioned_with_rabbit_mq -->
<a id='snippet-sample_global_partitioned_with_rabbit_mq'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UseRabbitMq();

        // Do something to add Saga storage too!

        opts
            .MessagePartitioning

            // This tells Wolverine to "just" use implied
            // message grouping based on Saga identity among other things
            .UseInferredMessageGrouping()

            .GlobalPartitioned(topology =>
            {
                // Creates 5 sharded RabbitMQ queues named "sequenced1" through "sequenced5"
                // with matching companion local queues for sequential processing
                topology.UseShardedRabbitQueues("sequenced", 5);
                topology.MessagesImplementing<MySequencedCommand>();

            });
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/RabbitMQ/Wolverine.RabbitMQ.Tests/Samples.cs#L718-L744' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_global_partitioned_with_rabbit_mq' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

### Excluding Message Types <Badge type="tip" text="6.25" />

`Except<T>()` carves a message type -- or a whole family, when given an interface or base class --
out of a topology, even when a broader rule like `MessagesImplementing<T>()` would otherwise match it.
Exclusions are checked first and win outright, so the result never depends on the order in which the
rules were declared.

```csharp
opts.MessagePartitioning
    .UseInferredMessageGrouping()
    .GlobalPartitioned(topology =>
    {
        topology.UseShardedRabbitQueues("sequenced", 5);
        topology.MessagesImplementing<IOrderCommand>();

        // ...but this one message type is re-published by this application
        // and must not re-enter the topology
        topology.Except<OrderStatusChanged>();
    });
```

The case this exists for is a message type that legitimately belongs to the topology *on the way in*,
but that the receiving application also re-publishes on its way somewhere else. Because each side
configures its own topology, excluding it on the **receiving** side keeps inbound partitioning intact
while stopping that application's own re-publish from re-entering the topology and arriving right back
at the handler that published it.

That loop is worth calling out, because it is invisible in configuration and shows up only as amplified
load. A handler ends with `bus.PublishAsync(message)` to forward the message to a UI over SignalR; the
message implements an interface that extends the marker the topology matches, so the re-publish draws
**two** routes -- the intended SignalR one, and the topology one that comes straight back to the same
handler. Note that Wolverine's route de-duplication does not catch this: it only removes explicit routes
to sticky-handler local queues.

::: tip
Excluding a type does **not** stop the application from *listening* for it on the topology's slots --
the companion-queue bridge is wired per endpoint, not per message type -- so inbound partitioning is
unaffected. That asymmetry is the whole point of the feature.
:::

### Validation

Wolverine validates global partitioning configuration at startup. It will throw an `InvalidOperationException` if:

- No message type matching policies are configured
- No external transport topology is configured
- The external and local topologies have different shard counts

### Native Per-Transport Alternatives

Global partitioning is the *portable* answer: it behaves identically on all ten transports because
Wolverine owns the slot assignment and the failover. Several brokers also ship a native primitive
that solves the same problem their own way, and on a single-broker system that can be the simpler
choice.

| Transport | Native primitive | How to use it |
|-----------|-----------------|---------------|
| Azure Service Bus | Sessions | [`RequireSessions()`](/guide/messaging/transports/azureservicebus/session-identifiers) with the session id set from your group id |
| Amazon SQS | FIFO `MessageGroupId` | A FIFO queue plus [`EnableFairQueueMessageGroups()`](/guide/messaging/transports/sqs/) |
| GCP Pub/Sub | Ordering keys | `EnableMessageOrdering`; Wolverine already maps the envelope's `GroupId` onto `OrderingKey` |
| Pulsar | `KeyShared` subscription | `SubscriptionType(SubscriptionType.KeyShared)` on the listener |
| Kafka | Partitions + consumer group | One topic with N partitions plus [`PropagateGroupIdToPartitionKey()`](/guide/messaging/transports/kafka) |

The important difference is the **unit of ordering**:

* **Global partitioning orders per _slot_.** Two unrelated group ids that hash to the same slot are
  serialized against each other. That is stronger than you asked for -- it costs some parallelism,
  but the number of slots is fixed, so there is no resource that grows with your key count.
* **Most native primitives order per _key_.** Sessions, message groups, ordering keys and
  `KeyShared` all allow unrelated keys to proceed in parallel, which is usually what you actually
  wanted. The trade is that the broker carries state per *active key*, so a system that mints a
  fresh group id per message will accumulate sessions/groups until it hits a service limit.

The second difference is **poison-message behavior**. Under a native per-key primitive, a message
that keeps failing blocks its entire key until it dead-letters. Under global partitioning it only
occupies one slot's companion local queue, which continues draining other group ids up to its
`MaxDegreeOfParallelism`.

::: tip Which should I use?
Reach for the native primitive when you are committed to one broker, you want unrelated keys to run
in parallel, and your group ids come from a bounded set (tenants, accounts, streams). Reach for
global partitioning when you want the same behavior across brokers, when your group id cardinality
is unbounded, or when a single poison message must not stall a key.
:::


