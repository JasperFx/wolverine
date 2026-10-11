# Topics

Wolverine supports publishing to [Rabbit MQ topic exchanges](https://www.rabbitmq.com/tutorials/tutorial-one-dotnet.html)
with this usage:

<!-- snippet: sample_publishing_to_rabbit_mq_topics_exchange -->
<a id='snippet-sample_publishing_to_rabbit_mq_topics_exchange'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UseRabbitMq();

        opts.Publish(x =>
        {
            x.MessagesFromNamespace("SomeNamespace");
            x.ToRabbitTopics("topics-exchange", ex =>
            {
                // optionally configure the exchange
            });
        });

        opts.ListenToRabbitQueue("");
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/RabbitMQ/Wolverine.RabbitMQ.Tests/Samples.cs#L18-L36' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_publishing_to_rabbit_mq_topics_exchange' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

While we're specifying the exchange name ("topics-exchange"), we did nothing to specify the topic
name. With this set up, when you publish a message in this application like so:

<!-- snippet: sample_sending_topic_routed_message -->
<a id='snippet-sample_sending_topic_routed_message'></a>
```cs
var publisher = host.MessageBus();
await publisher.SendAsync(new Message1());
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/RabbitMQ/Wolverine.RabbitMQ.Tests/Samples.cs#L38-L42' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_sending_topic_routed_message' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

You will be sending that message to the "topics-exchange" with a topic name derived from
the message type. By default that topic name will be Wolverine's [message type alias](/guide/messages.html#message-type-name-or-alias).
Unless explicitly overridden, that alias is the full type name of the message type.

That topic name derivation can be overridden explicitly by placing the `[Topic]` attribute
on a message type like so:

<!-- snippet: sample_using_topic_attribute -->
<a id='snippet-sample_using_topic_attribute'></a>
```cs
[Topic("one")]
public class TopicMessage1;
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Configuration/TopicRoutingTester.cs#L7-L11' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_topic_attribute' title='Start of snippet'>anchor</a></sup>
<a id='snippet-sample_using_topic_attribute-1'></a>
```cs
[Topic("color.blue")]
public class FirstMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/RabbitMQ/Wolverine.RabbitMQ.Tests/send_by_topics.cs#L498-L505' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_topic_attribute-1' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Of course, you can always explicitly send a message to a specific topic with this syntax:

<!-- snippet: sample_sending_to_a_specific_topic -->
<a id='snippet-sample_sending_to_a_specific_topic'></a>
```cs
await publisher.BroadcastToTopicAsync("color.*", new Message1());
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/RabbitMQ/Wolverine.RabbitMQ.Tests/Samples.cs#L44-L47' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_sending_to_a_specific_topic' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Note two things about the code above:

1. The `IMessageBus.BroadcastToTopicAsync()` method will fail if there is not the declared topic
   exchange endpoint that we configured above
2. You can use Rabbit MQ topic matching patterns in addition to using the exact topic

Lastly, to set up listening to specific topic names or topic patterns, you just need to
declare bindings between a topic name or pattern, the topics exchange, and the queues you're listening
to in your application. Lot of words, here's some code from the Wolverine test suite:

<!-- snippet: sample_binding_topics_and_topic_patterns_to_queues -->
<a id='snippet-sample_binding_topics_and_topic_patterns_to_queues'></a>
```cs
theSender = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UseRabbitMq("host=localhost;port=5672").AutoProvision();
        opts.PublishAllMessages().ToRabbitTopics("wolverine.topics", exchange =>
        {
            exchange.BindTopic("color.green").ToQueue("green");
            exchange.BindTopic("color.blue").ToQueue("blue");
            exchange.BindTopic("color.*").ToQueue("all");

            // Need this to be able to go to ONLY the green receiver for a test
            exchange.BindTopic("special").ToQueue("green");
        });

        opts.Discovery.DisableConventionalDiscovery()
            .IncludeType<TriggerTopicMessageHandler>();

        opts.ServiceName = "TheSender";

        opts.PublishMessagesToRabbitMqExchange<RoutedMessage>("wolverine.topics", m => m.TopicName);
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/RabbitMQ/Wolverine.RabbitMQ.Tests/send_by_topics.cs#L34-L57' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_binding_topics_and_topic_patterns_to_queues' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Publishing by Topic Rule

As of Wolverine 1.16, you can specify publishing rules for messages by supplying
the logic to determine the topic name from the message itself. Let's say that we have
an interface that several of our message types implement like so:

<!-- snippet: sample_rabbit_itenantmessage -->
<a id='snippet-sample_rabbit_itenantmessage'></a>
```cs
public interface ITenantMessage
{
    string TenantId { get; }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/RabbitMQ/Wolverine.RabbitMQ.Tests/Samples.cs#L658-L664' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_rabbit_itenantmessage' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Let's say that any message that implements that interface, we want published to the 
topic for that messages `TenantId`. We can implement that rule like so:

<!-- snippet: sample_rabbit_topic_rules -->
<a id='snippet-sample_rabbit_topic_rules'></a>
```cs
var builder = Host.CreateApplicationBuilder();
builder.UseWolverine(opts =>
{
    opts.UseRabbitMq();

    // Publish any message that implements ITenantMessage to
    // a Rabbit MQ "Topic" exchange named "tenant.messages"
    opts.PublishMessagesToRabbitMqExchange<ITenantMessage>("tenant.messages",
            m => $"{m.GetType().Name.ToLower()}/{m.TenantId}")

        // Specify or configure sending through Wolverine for all
        // messages through this Exchange
        .BufferedInMemory();
});

using var host = builder.Build();
await host.StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/RabbitMQ/Wolverine.RabbitMQ.Tests/Samples.cs#L544-L563' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_rabbit_topic_rules' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Publishing to a Topic Exchange and to a Local Queue at the Same Time <Badge type="tip" text="6.49" />

Let's say you want every message of a certain type to go out to a Rabbit MQ topic exchange with a topic computed from the message, but you
*also* want the same message handled right here in this application through a local queue. You might reach for
both `PublishMessagesToRabbitMqExchange<T>()` and an explicit `PublishMessage<T>().ToLocalQueue()` rule, but with the original overload the
exchange wins and the local queue rule is never even consulted. To make the exchange routing *additive* so
that Wolverine's other routing rules still apply, pass `additive: true`:

<!-- snippet: sample_publish_to_rabbitmq_exchange_and_local_queue -->
<a id='snippet-sample_publish_to_rabbitmq_exchange_and_local_queue'></a>
```cs
opts.UseRabbitMq().AutoProvision();

// Every AdditiveRabbitCommand goes to the topic exchange with a topic computed from the message...
opts.PublishMessagesToRabbitMqExchange<AdditiveRabbitCommand>(exchangeName,
    m => $"commands.{m.Region}", additive: true);

// ...and because that routing is additive, to the local "commands" queue at the same time
opts.PublishMessage<AdditiveRabbitCommand>().ToLocalQueue("commands");
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/RabbitMQ/Wolverine.RabbitMQ.Tests/additive_topic_exchange_routing.cs#L33-L44' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_publish_to_rabbitmq_exchange_and_local_queue' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

With that configuration, a single `PublishAsync()` sends the message to the topic exchange with the `commands.{Region}` topic and also drops it into the local
`commands` queue. You don't even need the explicit local queue rule if all you want is for a handler in this
application to get the message, because the conventional local routing to that handler applies as well -- just
like it would if the exchange routing weren't there at all.

::: tip
The original overload without the `additive` parameter is unchanged, and the exchange is the only route that
explicit publishing rules or local routing will leave those messages. If you're ever unsure what Wolverine is going
to do with a message type, `IWolverineRuntime.ExplainRoutingFor(type)` will walk you through every routing rule it
consulted. The additive routing shows up there as `[additive, pass-through]`. See
[message routing diagnostics](/guide/messaging/subscriptions#diagnostics) for more.
:::
