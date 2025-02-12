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
var builder = Host.CreateApplicationBuilder();

builder.UseWolverine(opts =>
{
    // Connect to the MQTT broker
    opts.UseMqtt(mqtt =>
    {
        var mqttServer = builder.Configuration["mqtt_server"];

        mqtt
            .WithMaxPendingMessages(3)
            .WithClientOptions(client => { client.WithTcpServer(mqttServer); });
    });

    // Listen to an MQTT topic, and this could also be a wildcard
    // pattern
    opts.ListenToMqttTopic("app/incoming")
        // In the case of receiving JSON data, but
        // not identifying metadata, tell Wolverine
        // to assume the incoming message is this type
        .DefaultIncomingMessage<Message1>()

        // The default is AtLeastOnce
        .QualityOfService(MqttQualityOfServiceLevel.AtMostOnce);

    // Publish messages to an outbound topic
    opts.PublishAllMessages()
        .ToMqttTopic("app/outgoing");
});

using var host = builder.Build();
await host.StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/MQTT/Wolverine.MQTT.Tests/Samples.cs#L14-L50' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_mqtt' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/MQTT/Wolverine.MQTT.Tests/Samples.cs#L116-L124' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_broadcast_to_mqtt' title='Start of snippet'>anchor</a></sup>
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
var builder = Host.CreateApplicationBuilder();

builder.UseWolverine(opts =>
{
    // Connect to the MQTT broker
    opts.UseMqtt(mqtt =>
    {
        var mqttServer = builder.Configuration["mqtt_server"];

        mqtt
            .WithMaxPendingMessages(3)
            .WithClientOptions(client => { client.WithTcpServer(mqttServer); });
    });

    // Publish messages to MQTT topics based on
    // the message type
    opts.PublishAllMessages()
        .ToMqttTopics()
        .QualityOfService(MqttQualityOfServiceLevel.AtMostOnce);
});

using var host = builder.Build();
await host.StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/MQTT/Wolverine.MQTT.Tests/Samples.cs#L87-L113' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_stream_events_to_mqtt_topics' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

In this approach, all messages will be routed to MQTT topics. The topic name for each message type
would be derived from either Wolverine's [message type name](/guide/messages.html#message-type-name-or-alias) rules
or by using the `[Topic("topic name")]` attribute as shown below:

<!-- snippet: sample_using_Topic_attribute -->
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

## Publishing by Topic Rules

You can publish messages to MQTT topics based on user defined logic to determine the actual topic name.

As an example, say you have a marker interfaces for your messages like this:

<!-- snippet: sample_mqtt_itenantmessage -->
<a id='snippet-sample_mqtt_itenantmessage'></a>
```cs
public interface ITenantMessage
{
    string TenantId { get; }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/MQTT/Wolverine.MQTT.Tests/Samples.cs#L196-L203' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_mqtt_itenantmessage' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

To publish any message implementing that interface to an MQTT topic, you could specify the topic name logic like this:

<!-- snippet: sample_mqtt_topic_rules -->
<a id='snippet-sample_mqtt_topic_rules'></a>
```cs
var builder = Host.CreateApplicationBuilder();

builder.UseWolverine(opts =>
{
    // Connect to the MQTT broker
    opts.UseMqtt(mqtt =>
    {
        var mqttServer = builder.Configuration["mqtt_server"];

        mqtt
            .WithMaxPendingMessages(3)
            .WithClientOptions(client => { client.WithTcpServer(mqttServer); });
    });

    // Publish any message that implements ITenantMessage to
    // MQTT with a topic derived from the message
    opts.PublishMessagesToMqttTopic<ITenantMessage>(m => $"{m.GetType().Name.ToLower()}/{m.TenantId}")

        // Specify or configure sending through Wolverine for all
        // MQTT topic broadcasting
        .QualityOfService(MqttQualityOfServiceLevel.ExactlyOnce)
        .BufferedInMemory();
});

using var host = builder.Build();
await host.StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/MQTT/Wolverine.MQTT.Tests/Samples.cs#L163-L192' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_mqtt_topic_rules' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Listening by Topic Filter

Wolverine supports topic filters for listening. The syntax is still just the same `ListenToMqttTopic(filter)` as shown
in this snippet from the Wolverine.MQTT test suite:

<!-- snippet: sample_listen_to_mqtt_topic_filter -->
<a id='snippet-sample_listen_to_mqtt_topic_filter'></a>
```cs
_receiver = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UseMqttWithLocalBroker(port);
        opts.ListenToMqttTopic("incoming/#").RetainMessages();
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/MQTT/Wolverine.MQTT.Tests/listen_with_topic_wildcards.cs#L41-L50' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_listen_to_mqtt_topic_filter' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

In the case of receiving any message that matches the topic filter *according to the [MQTT topic filter rules](https://cedalo.com/blog/mqtt-topics-and-mqtt-wildcards-explained/)*, that message
will be handled by the listening endpoint defined for that filter.

## Integrating with Non-Wolverine 

It's quite likely that in using Wolverine with an MQTT broker that you will be communicating with non-Wolverine systems
or devices on the other end, so you can't depend on the Wolverine metadata being sent in MQTT `UserProperties` data. Not to
worry, you've got options.

In the case of the external system sending you JSON, but nothing else, if you can design the system such that there's
only one type of message coming into a certain MQTT topic, you can just tell Wolverine to listen for that topic and
what that message type would be so that Wolverine is able to deserialize the message and relay that to the correct
message handler like so:

<!-- snippet: sample_listen_for_raw_json_to_mqtt -->
<a id='snippet-sample_listen_for_raw_json_to_mqtt'></a>
```cs
var builder = Host.CreateApplicationBuilder();
builder.UseWolverine(opts =>
{
    // Connect to the MQTT broker
    opts.UseMqtt(mqtt =>
    {
        var mqttServer = builder.Configuration["mqtt_server"];

        mqtt
            .WithMaxPendingMessages(3)
            .WithClientOptions(client => { client.WithTcpServer(mqttServer); });
    });

    // Listen to an MQTT topic, and this could also be a wildcard
    // pattern
    opts.ListenToMqttTopic("app/payments/made")
        // In the case of receiving JSON data, but
        // not identifying metadata, tell Wolverine
        // to assume the incoming message is this type
        .DefaultIncomingMessage<PaymentMade>();
});

using var host = builder.Build();
await host.StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/MQTT/Wolverine.MQTT.Tests/Samples.cs#L55-L82' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_listen_for_raw_json_to_mqtt' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

For more complex interoperability, you can implement the `IMqttEnvelopeMapper` interface in Wolverine to map between
incoming and outgoing MQTT messages and the Wolverine `Envelope` structure. Here's an example:

<!-- snippet: sample_MyMqttEnvelopeMapper -->
<a id='snippet-sample_mymqttenvelopemapper'></a>
```cs
public class MyMqttEnvelopeMapper : IMqttEnvelopeMapper
{
    public void MapEnvelopeToOutgoing(Envelope envelope, MqttApplicationMessage outgoing)
    {
        // This is the only absolutely mandatory item
        outgoing.PayloadSegment = envelope.Data;

        // Maybe enrich this more?
        outgoing.ContentType = envelope.ContentType;
    }

    public void MapIncomingToEnvelope(Envelope envelope, MqttApplicationMessage incoming)
    {
        // These are the absolute minimums necessary for Wolverine to function
        envelope.MessageType = typeof(PaymentMade).ToMessageTypeName();
        envelope.Data = incoming.PayloadSegment.Array;

        // Optional items
        envelope.DeliverWithin = 5.Seconds(); // throw away the message if it
        // is not successfully processed
        // within 5 seconds
    }

    public IEnumerable<string> AllHeaders()
    {
        yield break;
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/MQTT/Wolverine.MQTT.Tests/Samples.cs#L207-L238' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_mymqttenvelopemapper' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

And apply that to an MQTT topic like so:

<!-- snippet: sample_applying_custom_mqtt_envelope_mapper -->
<a id='snippet-sample_applying_custom_mqtt_envelope_mapper'></a>
```cs
var builder = Host.CreateApplicationBuilder();

builder.UseWolverine(opts =>
{
    // Connect to the MQTT broker
    opts.UseMqtt(mqtt =>
    {
        var mqttServer = builder.Configuration["mqtt_server"];

        mqtt
            .WithMaxPendingMessages(3)
            .WithClientOptions(client => { client.WithTcpServer(mqttServer); });
    });

    // Publish messages to MQTT topics based on
    // the message type
    opts.PublishAllMessages()
        .ToMqttTopics()

        // Tell Wolverine to map envelopes to MQTT messages
        // with our custom strategy
        .UseInterop(new MyMqttEnvelopeMapper())
        .QualityOfService(MqttQualityOfServiceLevel.AtMostOnce);
});

using var host = builder.Build();
await host.StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/MQTT/Wolverine.MQTT.Tests/Samples.cs#L128-L158' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_applying_custom_mqtt_envelope_mapper' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Clearing Out Retained Messages

MQTT brokers allow you to publish retained messages to a topic, meaning that the last message will always be retained by the 
broker and sent to any new subscribers. That's a little bit problematic if your Wolverine application happens to be restarted,
because that last retained message may easily be resent to your Wolverine application when you restart.

Not to fear, the MQTT protocol allows you to "clear" out a topic by sending it a zero byte message, and Wolverine has a couple
shortcuts for doing just that by returning a cascading message to "zero out" the topic a message was received on or a named topic like
this:

<!-- snippet: sample_ack_mqtt_topic -->
<a id='snippet-sample_ack_mqtt_topic'></a>
```cs
public static AckMqttTopic Handle(ZeroMessage message)
{
    // "Zero out" the topic that the original message was received from
    return new AckMqttTopic();
}

public static ClearMqttTopic Handle(TriggerZero message)
{
    // "Zero out" the designated topic
    return new ClearMqttTopic("red");
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/MQTT/Wolverine.MQTT.Tests/ack_smoke_tests.cs#L84-L98' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_ack_mqtt_topic' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->



