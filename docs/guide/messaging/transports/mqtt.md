# Using MQTT

::: warning
Wolverine requires the [V5 version of MQTT](https://docs.oasis-open.org/mqtt/mqtt/v5.0/mqtt-v5.0.html) for its broker support
:::

The Wolverine 1.9 release added a new transport option for the [MQTT standard](https://mqtt.org/) common in IoT Messaging.

## Installing

To use [MQTT](https://mqtt.org/) as a transport with Wolverine, first install the `Wolverine.MQTT` library via nuget to your project. Behind the scenes, this package uses the [MQTTnet](https://github.com/dotnet/MQTTnet) managed library for accessing MQTT brokers and also for its own testing.

```bash
dotnet add WolverineFx.Mqtt
```

In its most simplistic usage you enable the MQTT transport through calling the `WolverineOptions.UseMqtt()` extension method
and defining which MQTT topics you want to publish or subscribe to with the normal [subscriber rules](/guide/messaging/subscriptions) as 
shown in this sample:

<!-- snippet: sample_using_mqtt -->
<a id='snippet-sample_using_mqtt'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine((context, opts) =>
    {
        // Connect to the MQTT broker
        opts.UseMqtt(builder =>
        {
            var mqttServer = context.Configuration["mqtt_server"];

            builder
                .WithMaxPendingMessages(3)
                .WithClientOptions(client =>
                {
                    client.WithTcpServer(mqttServer);
                });
        });

        // Listen to an MQTT topic, and this could also be a wildcard
        // pattern
        opts.ListenToMqttTopic("app/incoming")
            
            // The default is AtLeastOnce
            .QualityOfService(MqttQualityOfServiceLevel.AtMostOnce);

        // Publish messages to an outbound topic
        opts.PublishAllMessages()
            .ToMqttTopic("app/outgoing");
    })
    .StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/MQTT/Wolverine.MQTT.Tests/Samples.cs#L10-L42' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_mqtt' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

::: info
The MQTT transport *at this time* only supports endpoints that are either `Buffered` or `Durable`. 
:::

::: warning
The MQTT transport does not really support the "Requeue" error handling policy in Wolverine. "Requeue" in this case becomes
effectively an inline "Retry"
:::

## Broadcast to User Defined Topics

As long as the MQTT transport is enabled in your application, you can explicitly publish messages to any named topic
through this usage:

<!-- snippet: sample_broadcast_to_mqtt -->
<a id='snippet-sample_broadcast_to_mqtt'></a>
```cs
public static async Task broadcast(IMessageBus bus)
{
    var paymentMade = new PaymentMade(200, "EUR");
    await bus.BroadcastToTopicAsync("region/europe/incoming", paymentMade);
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/MQTT/Wolverine.MQTT.Tests/Samples.cs#L77-L85' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_broadcast_to_mqtt' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Publishing to Derived Topic Names

::: info
The Wolverine is open to extending the options for determining the topic name from the message type,
but is waiting for feedback from the community before trying to build anything else around this.
:::

As a way of routing messages to MQTT topics, you also have this option:

<!-- snippet: sample_stream_events_to_mqtt_topics -->
<a id='snippet-sample_stream_events_to_mqtt_topics'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine((context, opts) =>
    {
        // Connect to the MQTT broker
        opts.UseMqtt(builder =>
        {
            var mqttServer = context.Configuration["mqtt_server"];

            builder
                .WithMaxPendingMessages(3)
                .WithClientOptions(client =>
                {
                    client.WithTcpServer(mqttServer);
                });
        });

        // Publish messages to MQTT topics based on
        // the message type
        opts.PublishAllMessages()
            .ToMqttTopics()
            .QualityOfService(MqttQualityOfServiceLevel.AtMostOnce);
    })
    .StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/MQTT/Wolverine.MQTT.Tests/Samples.cs#L48-L74' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_stream_events_to_mqtt_topics' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

In this approach, all messages will be routed to MQTT topics. The topic name for each message type
would be derived from either Wolverine's [message type name](/guide/messages.html#message-type-name-or-alias) rules
or by using the `[Topic("topic name")]` attribute as shown below:

<!-- snippet: sample_using_Topic_attribute -->
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/RabbitMQ/Wolverine.RabbitMQ.Tests/send_by_topics.cs#L150-L158' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_topic_attribute-1' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->





