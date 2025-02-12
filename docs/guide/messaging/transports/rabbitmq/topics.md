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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/RabbitMQ/Wolverine.RabbitMQ.Tests/Samples.cs#L15-L34' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_publishing_to_rabbit_mq_topics_exchange' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

While we're specifying the exchange name ("topics-exchange"), we did nothing to specify the topic
name. With this set up, when you publish a message in this application like so:

<!-- snippet: sample_sending_topic_routed_message -->
<a id='snippet-sample_sending_topic_routed_message'></a>
```cs
var publisher = host.MessageBus();
await publisher.SendAsync(new Message1());
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/RabbitMQ/Wolverine.RabbitMQ.Tests/Samples.cs#L36-L41' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_sending_topic_routed_message' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Configuration/TopicRoutingTester.cs#L7-L12' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_topic_attribute' title='Start of snippet'>anchor</a></sup>
<a id='snippet-sample_using_topic_attribute-1'></a>
```cs
[Topic("color.blue")]
public class FirstMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/RabbitMQ/Wolverine.RabbitMQ.Tests/send_by_topics.cs#L407-L415' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_topic_attribute-1' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Of course, you can always explicitly send a message to a specific topic with this syntax:

<!-- snippet: sample_sending_to_a_specific_topic -->
<a id='snippet-sample_sending_to_a_specific_topic'></a>
```cs
await publisher.BroadcastToTopicAsync("color.*", new Message1());
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/RabbitMQ/Wolverine.RabbitMQ.Tests/Samples.cs#L43-L47' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_sending_to_a_specific_topic' title='Start of snippet'>anchor</a></sup>
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
theSender = Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UseRabbitMq("host=localhost;port=5672").AutoProvision();
        opts.PublishAllMessages().ToRabbitTopics("wolverine.topics", exchange =>
        {
            exchange.BindTopic("color.green").ToQueue("green");
            exchange.BindTopic("color.blue").ToQueue("blue");
            exchange.BindTopic("color.*").ToQueue("all");
        });

        opts.PublishMessagesToRabbitMqExchange<RoutedMessage>("wolverine.topics", m => m.TopicName);
    }).Start();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/RabbitMQ/Wolverine.RabbitMQ.Tests/send_by_topics.cs#L26-L42' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_binding_topics_and_topic_patterns_to_queues' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/RabbitMQ/Wolverine.RabbitMQ.Tests/Samples.cs#L563-L570' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_rabbit_itenantmessage' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/RabbitMQ/Wolverine.RabbitMQ.Tests/Samples.cs#L478-L498' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_rabbit_topic_rules' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->
