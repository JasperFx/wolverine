# Using MQTT

::: warning
Wolverine requires the [V5 version of MQTT](https://docs.oasis-open.org/mqtt/mqtt/v5.0/mqtt-v5.0.html) for its broker support
:::

The Wolverine 1.9 release added a new transport option for the [MQTT standard](https://mqtt.org/) common in IoT Messaging.

## Installing

Wolverine's MQTT transport ships as **two mutually exclusive NuGet packages**. They are functionally identical — the same
`UseMqtt()` configuration and the same `Wolverine.MQTT` namespace and types — and differ *only* in which major version of the
underlying [MQTTnet](https://github.com/dotnet/MQTTnet) client library they depend on:

| Package | MQTTnet library version | Use when |
| --- | --- | --- |
| `WolverineFx.MQTT` | MQTTnet 4.x | The original package. Use it if you already depend on MQTTnet 4 elsewhere, or need to stay on it. |
| `WolverineFx.Mqtt5` | MQTTnet 5.x | Use it for new projects, or if you need MQTTnet 5. |

Install **exactly one** of them:

```bash
# Either the MQTTnet 4 package...
dotnet add package WolverineFx.MQTT
```

```bash
# ...or the MQTTnet 5 package (do not reference both)
dotnet add package WolverineFx.Mqtt5
```

::: warning
Reference **only one** of these packages. Because both expose the same types in the same `Wolverine.MQTT` namespace, every
sample on this page works unchanged with either one — but referencing both at once produces duplicate-type compiler errors.
The reason there are two packages at all is that a single assembly cannot depend on both MQTTnet 4 and MQTTnet 5: both ship an
assembly named `MQTTnet.dll`, so the two client majors cannot coexist in one build.

Note that "MQTTnet 5" (the client library version) is unrelated to the [MQTT v5 protocol](https://docs.oasis-open.org/mqtt/mqtt/v5.0/mqtt-v5.0.html)
required above — *both* Wolverine packages speak the MQTT v5 protocol.
:::

In its most simplistic usage you enable the MQTT transport through calling the `WolverineOptions.UseMqtt()` extension method
and defining which MQTT topics you want to publish or subscribe to with the normal [subscriber rules](/guide/messaging/subscriptions) as 
shown in this sample:

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

::: info
The MQTT transport *at this time* only supports endpoints that are either `Buffered` or `Durable`. 
:::

::: warning
The MQTT transport does not really support the "Requeue" error handling policy in Wolverine. "Requeue" in this case becomes
effectively an inline "Retry"
:::

## Connecting to Multiple MQTT Brokers

If a single Wolverine application needs to talk to more than one MQTT broker, register the additional broker(s)
with `AddNamedMqttBroker` using a `BrokerName`, then pin publishing or listening to a specific broker with the
`*OnNamedBroker` overloads:

```csharp
opts.UseMqtt(mqtt => mqtt.WithClientOptions(client => client.WithTcpServer("primary-broker")));

// An additional, independent MQTT broker identified by name
opts.AddNamedMqttBroker(new BrokerName("secondary"),
    mqtt => mqtt.WithClientOptions(client => client.WithTcpServer("secondary-broker")));

// Publish a message type to a topic on a named broker
opts.PublishMessage<OrderPlaced>()
    .ToMqttTopicOnNamedBroker(new BrokerName("secondary"), "orders");

// Listen to a topic on a named broker
opts.ListenToMqttTopicOnNamedBroker(new BrokerName("secondary"), "orders");
```

::: info
The Wolverine `Uri` scheme for any endpoint on a named broker is the broker name itself, so in the example above
you would see endpoint URIs like `secondary://topic/orders`. The default broker keeps the canonical `mqtt://`
scheme, which keeps the two brokers' endpoints from colliding.
:::

Each named broker is a completely separate `IManagedMqttClient` with its own per-node response topic, so
request/reply works independently over each broker.

Connecting to multiple named brokers is distinct from [broker-per-tenant multi-tenancy](#broker-per-tenant): a
named broker is a statically-addressed second connection that you target explicitly, whereas per-tenant
connections are selected at runtime from each message's tenant id.

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/MQTT/Wolverine.MQTT.Tests/Samples.cs#L113-L120' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_broadcast_to_mqtt' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/MQTT/Wolverine.MQTT.Tests/Samples.cs#L85-L110' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_stream_events_to_mqtt_topics' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

In this approach, all messages will be routed to MQTT topics. The topic name for each message type
would be derived from either Wolverine's [message type name](/guide/messages.html#message-type-name-or-alias) rules
or by using the `[Topic("topic name")]` attribute as shown below:

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/RabbitMQ/Wolverine.RabbitMQ.Tests/send_by_topics.cs#L500-L507' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_topic_attribute-1' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/MQTT/Wolverine.MQTT.Tests/Samples.cs#L191-L197' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_mqtt_itenantmessage' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/MQTT/Wolverine.MQTT.Tests/Samples.cs#L159-L187' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_mqtt_topic_rules' title='Start of snippet'>anchor</a></sup>
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
        opts.ListenToMqttTopic("incoming/one", "group1").RetainMessages();
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/MQTT/Wolverine.MQTT.Tests/listen_with_emqx_shared_group_topic.cs#L42-L50' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_listen_to_mqtt_topic_filter' title='Start of snippet'>anchor</a></sup>
<a id='snippet-sample_listen_to_mqtt_topic_filter-1'></a>
```cs
_receiver = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UseMqttWithLocalBroker(port);
        opts.ListenToMqttTopic("incoming/#").RetainMessages();
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/MQTT/Wolverine.MQTT.Tests/listen_with_topic_wildcards.cs#L42-L50' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_listen_to_mqtt_topic_filter-1' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/MQTT/Wolverine.MQTT.Tests/Samples.cs#L54-L80' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_listen_for_raw_json_to_mqtt' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

For more complex interoperability, you can implement the `IMqttEnvelopeMapper` interface in Wolverine to map between
incoming and outgoing MQTT messages and the Wolverine `Envelope` structure. Here's an example:

<!-- snippet: sample_mymqttenvelopemapper -->
<a id='snippet-sample_mymqttenvelopemapper'></a>
```cs
public class MyMqttEnvelopeMapper : IMqttEnvelopeMapper
{
    public void MapEnvelopeToOutgoing(Envelope envelope, MqttApplicationMessage outgoing)
    {
        // This is the only absolutely mandatory item
        outgoing.PayloadSegment = envelope.Data!;

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
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/MQTT/Wolverine.MQTT.Tests/Samples.cs#L201-L226' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_mymqttenvelopemapper' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/MQTT/Wolverine.MQTT.Tests/Samples.cs#L124-L154' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_applying_custom_mqtt_envelope_mapper' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/MQTT/Wolverine.MQTT.Tests/ack_smoke_tests.cs#L89-L102' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_ack_mqtt_topic' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Authentication via OAuth2

Wolverine supports MQTT v5 OAuth2/JWT authentication by supplying a token callback and refresh interval when you configure
the transport. The callback returns raw token bytes (use UTF-8 encoding if your token is a string). When configured,
Wolverine sets the MQTT authentication method to `OAUTH2-JWT`, sends the initial token with the connect packet, and
re-authenticates on the configured refresh period while the client is connected.

::: info
You don't need to configure `AuthenticationMethod` and `AuthenticationData` by yourself. These are overriden when the `MqttJwtAuthenticationOptions` parameter is set.
:::

Minimal configuration example:
```cs
var builder = Host.CreateApplicationBuilder();

builder.UseWolverine(opts =>
{
    opts.UseMqtt(
        mqtt => mqtt.WithClientOptions(client => client.WithTcpServer("broker")),
        new MqttJwtAuthenticationOptions(
            async () => Encoding.UTF8.GetBytes(await GetJwtTokenAsync()),
            30.Minutes()));
});
```

## Broker per Tenant

Named brokers (above) are a *static* topology: you pin specific endpoints to a specific broker by name at
configuration time. **Broker-per-tenant** is different — it is *runtime* routing. You declare one shared topic
topology, and each tenant is served by its **own dedicated MQTT connection** (a distinct broker). Which
connection a message goes to (and which connection an inbound message came from) is decided at runtime by the
message's [tenant id](/guide/handlers/multi-tenancy), typically set through `DeliveryOptions.TenantId`:

```csharp
opts.UseMqtt(mqtt => mqtt.WithClientOptions(client => client.WithTcpServer("shared-broker")))

    // How should Wolverine route a message whose TenantId is null or unknown?
    // FallbackToDefault (the default) uses the shared connection; TenantIdRequired
    // throws; IgnoreUnknownTenants silently drops it.
    .TenantIdBehavior(TenantedIdBehavior.FallbackToDefault)

    // Each tenant gets its OWN dedicated MQTT connection, but shares the
    // topic topology declared below.
    .AddTenant("tenant-west",
        mqtt => mqtt.WithClientOptions(client => client.WithTcpServer("west-broker")))

    .AddTenant("tenant-east",
        mqtt => mqtt.WithClientOptions(client => client.WithTcpServer("east-broker")));

// One shared topology; messages are routed to the right connection at runtime by
// Envelope.TenantId (e.g. new DeliveryOptions { TenantId = "tenant-west" }).
opts.PublishMessage<OrderPlaced>().ToMqttTopic("orders");
opts.ListenToMqttTopic("orders");
```

To route a specific message to a tenant's connection, stamp the tenant id on the send:

```csharp
await bus.SendAsync(new OrderPlaced("blue"), new DeliveryOptions { TenantId = "tenant-west" });
```

Wolverine wraps the outbound endpoint in a `TenantedSender` that dispatches on `Envelope.TenantId`, and builds a
compound listener that runs one subscription per tenant connection — each inbound envelope is stamped with the
tenant id of the connection it was consumed from. This mirrors the
[RabbitMQ](/guide/messaging/transports/rabbitmq/multi-tenancy) and
[Azure Service Bus](/guide/messaging/transports/azureservicebus/multitenancy) broker-per-tenant support.

::: warning Unique ClientId per tenant (mandatory)
MQTT brokers forcibly disconnect a second connection that shares a `ClientId`. Wolverine therefore always gives
each tenant connection a **unique** `ClientId` derived from the tenant id (`<clientId>-tenant-<tenantId>`), even
if you pre-set one on the tenant options. This is required so tenant connections never kick each other.
:::

::: info Shared topology, isolated broker
Unlike a shared-broker/topic-prefix multi-tenancy model, MQTT tenants use the **same topic string** on each
tenant's **own broker** — isolation is purely a matter of which broker the tenant's connection points at. One
consequence: [retained messages](#clearing-out-retained-messages) are per-broker, so a retained message on one
tenant's broker is not visible to any other tenant.
:::

::: tip Named broker vs. broker-per-tenant
Use a **named broker** when a *fixed set of endpoints* should always talk to a *specific* broker. Use
**broker-per-tenant** when the *same logical endpoints* should be transparently routed to a *different connection
per tenant* based on the runtime tenant id. They are independent features and can be combined.
:::

## Interoperability

::: tip
Also see the more generic [Wolverine Guide on Interoperability](/tutorials/interop)
:::

The Wolverine MQTT transport supports pluggable interoperability strategies through the `Wolverine.MQTT.IMqttEnvelopeMapper`
interface to map from Wolverine's `Envelope` structure and MQTT's `MqttApplicationMessage` structure.

Here's a simple example:

<!-- snippet: sample_mymqttenvelopemapper -->
<a id='snippet-sample_mymqttenvelopemapper'></a>
```cs
public class MyMqttEnvelopeMapper : IMqttEnvelopeMapper
{
    public void MapEnvelopeToOutgoing(Envelope envelope, MqttApplicationMessage outgoing)
    {
        // This is the only absolutely mandatory item
        outgoing.PayloadSegment = envelope.Data!;

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
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/MQTT/Wolverine.MQTT.Tests/Samples.cs#L201-L226' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_mymqttenvelopemapper' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

You will need to apply that mapper to each endpoint like so:

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/MQTT/Wolverine.MQTT.Tests/Samples.cs#L124-L154' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_applying_custom_mqtt_envelope_mapper' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## URI reference

The `MqttEndpointUri` helper class builds canonical endpoint URIs:

| URI form | Helper call |
|---|---|
| `mqtt://topic/{name}` | `MqttEndpointUri.Topic("name")` |

```csharp
using Wolverine.MQTT;

var uri = MqttEndpointUri.Topic("sensor/temperature");
```
