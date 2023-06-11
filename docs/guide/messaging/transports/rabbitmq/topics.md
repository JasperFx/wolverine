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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Wolverine.RabbitMQ.Tests/Samples.cs#L15-L34' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_publishing_to_rabbit_mq_topics_exchange' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

While we're specifying the exchange name ("topics-exchange"), we did nothing to specify the topic
name. With this set up, when you publish a message in this application like so:

<!-- snippet: sample_sending_topic_routed_message -->
<a id='snippet-sample_sending_topic_routed_message'></a>
```cs
var publisher = host.Services.GetRequiredService<IMessageBus>();
await publisher.SendAsync(new Message1());
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Wolverine.RabbitMQ.Tests/Samples.cs#L36-L41' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_sending_topic_routed_message' title='Start of snippet'>anchor</a></sup>
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
public class TopicMessage1
{
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Configuration/TopicRoutingTester.cs#L8-L15' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_topic_attribute' title='Start of snippet'>anchor</a></sup>
<a id='snippet-sample_using_topic_attribute-1'></a>
```cs
[Topic("color.blue")]
public class FirstMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Wolverine.RabbitMQ.Tests/send_by_topics.cs#L150-L158' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_topic_attribute-1' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Of course, you can always explicitly send a message to a specific topic with this syntax:

<!-- snippet: sample_sending_to_a_specific_topic -->
<a id='snippet-sample_sending_to_a_specific_topic'></a>
```cs
await publisher.BroadcastToTopicAsync("color.*", new Message1());
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Wolverine.RabbitMQ.Tests/Samples.cs#L43-L47' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_sending_to_a_specific_topic' title='Start of snippet'>anchor</a></sup>
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
    }).Start();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Wolverine.RabbitMQ.Tests/send_by_topics.cs#L24-L38' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_binding_topics_and_topic_patterns_to_queues' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->
