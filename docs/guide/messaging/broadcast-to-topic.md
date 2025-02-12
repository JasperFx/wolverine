# Broadcast Messages to a Specific Topic

If you're using a transport endpoint that supports publishing messages by topic
such as this example using Rabbit MQ from the Wolverine tests:

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

You can explicitly publish a message to a topic through this syntax:

<!-- snippet: sample_send_to_topic -->
<a id='snippet-sample_send_to_topic'></a>
```cs
var publisher = theSender.Services
    .GetRequiredService<IMessageBus>();

await publisher.BroadcastToTopicAsync("color.purple", new Message1());
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/RabbitMQ/Wolverine.RabbitMQ.Tests/send_by_topics.cs#L100-L107' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_send_to_topic' title='Start of snippet'>anchor</a></sup>
<a id='snippet-sample_send_to_topic-1'></a>
```cs
var publisher = theSender.Services
    .GetRequiredService<IMessageBus>();

await publisher.BroadcastToTopicAsync("color.purple", new Message1());
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/RabbitMQ/Wolverine.RabbitMQ.Tests/send_by_topics.cs#L283-L290' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_send_to_topic-1' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->


::: warning
If you wish to use this functionality, you have to configure at least one sending endpoint subscription like a Rabbit MQ
topic exchange in your application. Wolverine has to know how to send messages with your topic.
:::

## Topic Sending as Cascading Message

Wolverine is pretty serious about enabling as many message handlers or HTTP endpoints as possible to be [pure functions](https://en.wikipedia.org/wiki/Pure_function)
where the unit testing is easier, so there's an option to broadcast messages to a particular topic as a cascaded message:

<!-- snippet: sample_cascaded_to_topic_message -->
<a id='snippet-sample_cascaded_to_topic_message'></a>
```cs
public class ManuallyRoutedTopicResponseHandler
{
    public IEnumerable<object> Consume(MyMessage message, Envelope envelope)
    {
        // Go North now at the "direction" queue
        yield return new GoNorth().ToTopic($"direction/{envelope.TenantId}");
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/CascadingSamples.cs#L161-L172' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_cascaded_to_topic_message' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->
