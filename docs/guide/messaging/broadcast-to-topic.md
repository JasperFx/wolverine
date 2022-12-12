# Broadcast Messages to a Specific Topic

If you're using a transport endpoint that supports publishing messages by topic
such as this example using Rabbit MQ from the Wolverine tests:

<!-- snippet: sample_binding_topics_and_topic_patterns_to_queues -->
<a id='snippet-sample_binding_topics_and_topic_patterns_to_queues'></a>
```cs
theSender = Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UseRabbitMq().AutoProvision();
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

You can explicitly publish a message to a topic through this syntax:

<!-- snippet: sample_send_to_topic -->
<a id='snippet-sample_send_to_topic'></a>
```cs
var publisher = theSender.Services
    .GetRequiredService<IMessageBus>();

await publisher.BroadcastToTopicAsync("color.purple", new Message1());
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Wolverine.RabbitMQ.Tests/send_by_topics.cs#L72-L79' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_send_to_topic' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->


## Scheduling Message Delivery

TODO -- write stuff here



