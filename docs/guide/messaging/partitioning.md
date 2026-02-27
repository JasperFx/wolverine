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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/PartitioningSamples.cs#L140-L150' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_order_commands_for_partitioning' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/PartitioningSamples.cs#L45-L73' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_opting_into_local_partitioned_routing' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

So let's talk about what we set up in the code above. First, we've taught Wolverine how to determine the group
id of any message that implements the `IOrderCommand` interface. Next we've told Wolverine to publish any message
implementing our `IOrderCommand` interface to one of four [local queues](/guide/messaging/transports/local) named "orders1", "orders2", "orders3", and "orders4."
At runtime, when you publish an `IOrderCommand` within the system, Wolverine will determine the group id of the new message through the `IOrderCommand.OrderId` rule we created 
(it does get written to `Envelope.GroupId`). Once Wolverine has that `GroupId`, it needs to determine which of the "orders#"
queues to send the message, and the easiest way to explain this is really just to show the internal code:

<!-- snippet: sample_SlotForSending -->
<a id='snippet-sample_SlotForSending'></a>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Wolverine/Runtime/Partitioning/PartitionedMessagingExtensions.cs#L17-L42' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_SlotForSending' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/MartenTests/concurrency_resilient_sharded_processing.cs#L112-L134' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_inferred_message_group_id' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The built in rules *at this point* include:

* Using the Sage identity of a message that is handled by a [Stateful Saga](/guide/durability/sagas)
* Using the stream/aggregate id of messages that are part of the [Aggregate Handler Workflow](/guide/durability/marten/event-sourcing) integration with Marten

## Specifying Grouping Rules

Internally, Wolverine is using a list of implementations of this interface:

<!-- snippet: sample_IGroupingRule -->
<a id='snippet-sample_IGroupingRule'></a>
```cs
/// <summary>
/// Strategy for determining the GroupId of a message
/// </summary>
public interface IGroupingRule
{
    bool TryFindIdentity(Envelope envelope, out string groupId);
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Wolverine/Runtime/Partitioning/IGroupingRule.cs#L3-L13' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_IGroupingRule' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/PartitioningSamples.cs#L86-L110' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_configuring_message_grouping_rules' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/PartitioningSamples.cs#L113-L122' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_send_message_with_group_id' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/PartitioningSamples.cs#L130-L137' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_with_group_id_as_cascading_message' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/PartitioningSamples.cs#L45-L73' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_opting_into_local_partitioned_routing' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/PartitioningSamples.cs#L14-L40' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_configuring_partitioned_processing_on_any_listener' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

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

At this point Wolverine has direct support for partitioned routing to Rabbit MQ or Amazon SQS. Note that in both
of the following examples, Wolverine is both setting up publishing rules out to these queues, and also configuring
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/RabbitMQ/Wolverine.RabbitMQ.Tests/concurrency_resilient_sharded_processing.cs#L71-L93' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_defining_partitioned_routing_for_rabbitmq' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/AWS/Wolverine.AmazonSqs.Tests/concurrency_resilient_sharded_processing.cs#L72-L87' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_partitioned_publishing_through_amazon_sqs' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Partitioning Messages Received from External Systems

::: warning
Brute force, no points for style, explicit coding ahead!
:::

If you are receiving messages from an external source that will be vulnerable to concurrent access problems when the messages
are executed, but you either do not want to make the external system publish the group ids or have no ability to make the 
upstream system care about your own internal group id details, you can simply relay the received messages back out
to a partitioned message topology owned by your system.

Using Amazon SQS as our transport, lets say that we're receiving messages from the external system at one queue like this:

Hey folks, more coming soon. Hopefully before Wolverine 5.0.

Watch this issue: https://github.com/JasperFx/wolverine/issues/1728



