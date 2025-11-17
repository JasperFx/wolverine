# Interoperability with Non Wolverine Systems

::: warning
We greatly expanded the interoperability options in Wolverine for 5.0, but some of the integrations may not have widely been used
in real applications outside of testing by the time you try to use especially the MassTransit or NServiceBus for transports besides
Rabbit MQ or CloudEvents with any transport. Please feel free to post issues to GitHub or use the Discord server to report
any issues.
:::

It's a complicated world, Wolverine is a relative newcomer in the asynchronous messaging space in the .NET ecosystem,
and who knows what other systems on completely different technical platforms you might have going on. As Wolverine has
gained adoption, and as a prerequisite for other folks to even consider adopting Wolverine, we've had to improve Wolverine's 
ability to exchange messages with non-Wolverine systems. We hope this guide will answer any questions you might have
about how to leverage interoperability with Wolverine and non-Wolverine systems.

As is typical for messaging tools, Wolverine has an internal ["envelope wrapper"](https://www.enterpriseintegrationpatterns.com/patterns/messaging/EnvelopeWrapper.html) structure called `Envelope`
that holds the .NET message object and/or the binary representation of the message and all known metadata about the message like:

* Correlation information
* The [message type name for Wolverine](/guide/messages.html#message-type-name-or-alias)
* The number of attempts in case of failures
* When a message was originally sent
* The content type of any serialized data
* Topic name, group id, and deduplication id for transports that can use that information
* Information about expected replies and a `ReplyUri` that tells Wolverine where to send any responses to the current message
* Other headers

Here's a little sample of how an `Envelope` might be used internally by Wolverine:

<!-- snippet: sample_create_an_outgoing_envelope -->
<a id='snippet-sample_create_an_outgoing_envelope'></a>
```cs
var message = new ApproveInvoice("1234");

// I'm really creating an outgoing message here
var envelope = new Envelope(message);

// This information is assigned internally,
// but it's good to know that it exists
envelope.CorrelationId = "AAA";

// This would refer to whatever Wolverine message
// started a set of related activity
envelope.ConversationId = Guid.NewGuid();

// For both outgoing and incoming messages,
// this identifies how the message data is structured
envelope.ContentType = "application/json";

// When using multi-tenancy, this is used to track
// what tenant a message applies to
envelope.TenantId = "222";

// Not every broker cares about this of course
envelope.GroupId = "BBB";
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/InteropSamples.cs#L9-L35' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_create_an_outgoing_envelope' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

As you can probably imagine, Wolverine uses this structure all throughout its internals to handle, send, track, and otherwise
coordinate message processing. When using Wolverine with external transport brokers like Kafka, Pulsar, Google Pubsub,
or Rabbit MQ, Wolverine goes through a bi-directional mapping from whatever each broker's own representation of a "message"
is to Wolverine's own `Envelope` structure. Likewise, when Wolverine sends messages through an external messaging broker,
it's having to map its `Envelope` to the transport's outgoing message structure as shown below:

![Envelope Mapping](/envelope-mappers.png)

As you can probably surmise from the diagram, there's an important abstraction in Wolverine called an "envelope mapper"
that does the work of translating Wolverine's `Envelope` structure to and from each message broker's own model for messages.

These abstractions are a little bit different for each external broker, and Wolverine provides some built in mappers for
common interoperability scenarios:

| Transport                                                         | Envelope Mapper Name                                                                           | Built In Interop                                |
|-------------------------------------------------------------------|------------------------------------------------------------------------------------------------|-------------------------------------------------|
| [Rabbit MQ](/guide/messaging/transports/rabbitmq/)                | [IRabbitMqEnvelpoeMapper](/guide/messaging/transports/rabbitmq/interoperability)               | MassTransit, NServiceBus, CloudEvents, Raw Json |
| [Azure Service Bus](/guide/messaging/transports/azureservicebus/) | [IAzureServiceBusEnvelopeMapper](/guide/messaging/transports/azureservicebus/interoperability) | MassTransit, NServiceBus, CloudEvents, Raw Json |
| [Amazon SQS](/guide/messaging/transports/sqs/)                    | [ISqsEnvelopeMapper](/guide/messaging/transports/sqs/interoperability)                         | MassTransit, NServiceBus, CloudEvents, Raw Json |        
| [Amazon SNS](/guide/messaging/transports/sns) | [ISnsEnvelopeMapper](/guide/messaging/transports/sns.html#interoperability)                    | MassTransit, NServiceBus, CloudEvents, Raw Json |
| [Kafka](/guide/messaging/transports/kafka) | [IKafkaEnvelopeMapper](/guide/messaging/transports/kafka.html#interoperability)                | CloudEvents, Raw Json                           |
| [Apache Pulsar](/guide/messaging/transports/pulsar) | [IPulsarEnvelopeMapper](/guide/messaging/transports/pulsar.html#interoperability)              | CloudEvents                                     | 
| [MQTT](/guide/messaging/transports/mqtt) | [IMqttEnvelopeMapper](/guide/messaging/transports/mqtt.html#interoperability)]                 | CloudEvents                                     |
| [Redis](/guide/messaging/transports/redis) | [IRedisEnvelopeMapper](/guide/messaging/transports/redis.html#interoperability) | CloudEvents                                     |

## Writing a Custom Envelope Mapper

Let's say that you're needing to interact with an upstream system that publishes messages to Wolverine
through an external message broker in a format that's 
completely different than what Wolverine itself uses or any built in envelope mapping recipe -- which is actually quite common. 

When you map incoming transport messages to Wolverine's `Envelope`, **at a bare minimum**, Wolverine needs to know the binary data that Wolverine will later try to deserialize to a .NET type 
in its own execution pipeline (`Envelope.Data`) and how to read that binary data into a .NET message object. When Wolverine
tries to handle an incoming `Envelope` in its execution pipeline, it will:

1. Start some Open Telemetry span tracking using the metadata from the incoming `Envelope` to create traceability between the
   upstream publisher and the current message execution. You don't *have* to support this in your custom mapper, but you'd ideally *like* to have this.
2. Checks if the `Envelope` has expired based on its `DeliverBy` property, and discards the `Envelope` if so
3. Tries to choose a [message serializer](https://wolverinefx.net/guide/messages.html) based on the `Envelope.Serializer`, then the matching serializer based on `Envelope.ContentType` if that exists, then it falls through to
   the default serializer for the application (SystemTextJson by default) just in case the default serializer.

As is hopefully clear from that series of steps above, when you are writing to the incoming `Envelope` in a custom message,
you have to set the binary data for the incoming message, you'd ideally like to set the correlation information on `Envelope`
to reflect the incoming data, and you need to either set at least `Envelope.MessageType` so Wolverine knows what
message type to try to deserialize to, or just set a specific `IMessageSerializer` on `Envelope.Serializer` that Wolverine
assumes will "know" how to build out the right type and maybe even infer more valuable metadata to the `Envelope` from
the raw binary data (the MassTransit and CloudEvents interoperability works this way).

In this first sample, I'm going to write a simplistic mapper for Kafka that assumes everything coming into an 
endpoint is JSON and a specific type:

<!-- snippet: sample_OurKafkaJsonMapper -->
<a id='snippet-sample_ourkafkajsonmapper'></a>
```cs
// Simplistic envelope mapper that expects every message to be of
// type "T" and serialized as JSON that works perfectly well w/ our
// application's default JSON serialization
public class OurKafkaJsonMapper<TMessage> : IKafkaEnvelopeMapper
{
    // Wolverine needs to know the 
    private readonly string _messageTypeName = typeof(TMessage).ToMessageTypeName();

    // Map the Wolverine Envelope structure to the outgoing Kafka structure
    public void MapEnvelopeToOutgoing(Envelope envelope, Message<string, byte[]> outgoing)
    {
        // We'll come back to this later...
        throw new NotSupportedException();
    }

    // Map the incoming message from Kafka to the incoming Wolverine envelope
    public void MapIncomingToEnvelope(Envelope envelope, Message<string, byte[]> incoming)
    {
        // We're making an assumption here that only one type of message
        // is coming in on this particular Kafka topic, so we're telling
        // Wolverine what the message type is according to Wolverine's own
        // message naming scheme
        envelope.MessageType = _messageTypeName;

        // Tell Wolverine to use JSON serialization for the message 
        // data
        envelope.ContentType = "application/json";

        // Put the raw binary data right on the Envelope where
        // Wolverine "knows" how to get at it later
        envelope.Data = incoming.Value;
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Kafka/Wolverine.Kafka.Tests/DocumentationSamples.cs#L167-L203' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_ourkafkajsonmapper' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Which is essentially how the built in "Raw JSON" mapper works in external transport mappers. In the envelope mapper above
we can assume that the actual message data is something that a straightforward serializer can deal with the raw data, and
we really just need to deal with setting a few headers.

In some cases you might just have to do a little bit different mapping of header information to `Envelope` properties
than Wolverine's built in protocol. For most transports (Amazon SQS and SNS are the exceptions), you can just modify
the "header name to Envelope" mappings something like this example from Azure Service Bus:

<!-- snippet: sample_customized_envelope_mapping -->
<a id='snippet-sample_customized_envelope_mapping'></a>
```cs
var builder = Host.CreateApplicationBuilder();
builder.UseWolverine(opts =>
{
    // One way or another, you're probably pulling the Azure Service Bus
    // connection string out of configuration
    var azureServiceBusConnectionString = builder
        .Configuration
        .GetConnectionString("azure-service-bus");

    // Connect to the broker in the simplest possible way
    opts.UseAzureServiceBus(azureServiceBusConnectionString).AutoProvision();

    // I overrode the buffering limits just to show
    // that they exist for "back pressure"
    opts.ListenToAzureServiceBusQueue("incoming")
        .UseInterop((queue, mapper) =>
        {
            // Not sure how useful this would be, but we can start from
            // the baseline Wolverine mapping and just override a few mappings
            mapper.MapPropertyToHeader(x => x.ContentType, "OtherTool.ContentType");
            mapper.MapPropertyToHeader(x => x.CorrelationId, "OtherTool.CorrelationId");
            // and more
            
            // or a little uglier where you might be mapping and transforming data between
            // the transport's model and the Wolverine Envelope
            mapper.MapProperty(x => x.ReplyUri, 
                (e, msg) => e.ReplyUri = new Uri($"asb://queue/{msg.ReplyTo}"),
                (e, msg) => msg.ReplyTo = "response");
            
        });

});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Azure/Wolverine.AzureServiceBus.Tests/DocumentationSamples.cs#L466-L501' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_customized_envelope_mapping' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

That code isn't necessarily for the feint of heart, but that will sometimes be an easier recipe than trying to write
a custom mapper from scratch. The NServiceBus interoperability for everything but Amazon SQS/SNS transports uses this 
approach:

<!-- snippet: sample_show_the_NServiceBus_mapping -->
<a id='snippet-sample_show_the_nservicebus_mapping'></a>
```cs
public void UseNServiceBusInterop()
{
    // We haven't tried to address this yet, but NSB can stick in some characters
    // that STJ chokes on, but good ol' Newtonsoft handles just fine
    DefaultSerializer = new NewtonsoftSerializer(new JsonSerializerSettings());
    
    customizeMapping((m, _) =>
    {
        m.MapPropertyToHeader(x => x.ConversationId, "NServiceBus.ConversationId");
        m.MapPropertyToHeader(x => x.SentAt, "NServiceBus.TimeSent");
        m.MapPropertyToHeader(x => x.CorrelationId!, "NServiceBus.CorrelationId");

        var replyAddress = new Lazy<string>(() =>
        {
            var replyEndpoint = (RabbitMqEndpoint)_parent.ReplyEndpoint()!;
            return replyEndpoint.RoutingKey();
        });

        void WriteReplyToAddress(Envelope e, IBasicProperties props)
        {
            props.Headers["NServiceBus.ReplyToAddress"] = replyAddress.Value;
        }

        void ReadReplyUri(Envelope e, IReadOnlyBasicProperties props)
        {
            if (props.Headers.TryGetValue("NServiceBus.ReplyToAddress", out var raw))
            {
                var queueName = (raw is byte[] b ? Encoding.Default.GetString(b) : raw.ToString())!;
                e.ReplyUri = new Uri($"{_parent.Protocol}://queue/{queueName}");
            }
        }

        m.MapProperty(x => x.ReplyUri!, ReadReplyUri, WriteReplyToAddress);
    });
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/RabbitMQ/Wolverine.RabbitMQ/Internal/RabbitMqEndpoint.NServiceBus.cs#L10-L48' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_show_the_nservicebus_mapping' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Finally, here's another example that works quite differently where the mapper sets a serializer directly on the `Envelope`:

<!-- snippet: sample_MassTransitMapper_for_SQS -->
<a id='snippet-sample_masstransitmapper_for_sqs'></a>
```cs
// This guy is the envelope mapper for interoperating
// with MassTransit 
internal class MassTransitMapper : ISqsEnvelopeMapper
{
    private readonly IMassTransitInteropEndpoint _endpoint;
    private MassTransitJsonSerializer _serializer;

    public MassTransitMapper(IMassTransitInteropEndpoint endpoint)
    {
        _endpoint = endpoint;
        _serializer = new MassTransitJsonSerializer(endpoint);
    }

    public MassTransitJsonSerializer Serializer => _serializer;

    public string BuildMessageBody(Envelope envelope)
    {
        return Encoding.UTF8.GetString(_serializer.Write(envelope));
    }

    public IEnumerable<KeyValuePair<string, MessageAttributeValue>> ToAttributes(Envelope envelope)
    {
        yield break;
    }

    public void ReadEnvelopeData(Envelope envelope, string messageBody, IDictionary<string, MessageAttributeValue> attributes)
    {
        // TODO -- this could be more efficient of course
        envelope.Data = Encoding.UTF8.GetBytes(messageBody);
        
        // This is the really important part
        // of the mapping
        envelope.Serializer = _serializer;
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/AWS/Wolverine.AmazonSqs/Internal/MassTransitMapper.cs#L7-L45' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_masstransitmapper_for_sqs' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

In the case above, the `MassTransitSerializer` is a two step process that first deserializes a JSON document that contains
metadata about the message and also embedded JSON for the actual message, then figures out the proper message type to deserialize
the inner JSON and *finally* sends the real message and all the expected correlation metadata about the message on to
Wolverine's execution pipeline in such a way that Wolverine can create traceability between MassTransit on the other side and 
Wolverine. 

## Interop with MassTransit

AWS SQS, Azure Service Bus, or Rabbit MQ can interoperate with MassTransit by opting into this setting on an endpoint
by endpoint basis as shown in this sample with Rabbit MQ:

<!-- snippet: sample_rabbitmq_interop_with_masstransit -->
<a id='snippet-sample_rabbitmq_interop_with_masstransit'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // *A* way to configure Rabbit MQ using their Uri schema
        // documented here: https://www.rabbitmq.com/uri-spec.html
        opts.UseRabbitMq(new Uri("amqp://localhost"));

        // Set up a listener for a queue
        opts.ListenToRabbitQueue("incoming1")
            
            // There is a limitation here in that you will also
            // have to tell Wolverine what the message type is
            // because it cannot today figure out what the Wolverine
            // message type in the current application is from 
            // MassTransit's metadata
            .DefaultIncomingMessage<Message1>()
            .UseMassTransitInterop(
                
                // This is optional, but just letting you know it's there
                interop =>
                {
                    interop.UseSystemTextJsonForSerialization(stj =>
                    {
                        // Don't worry all of this is optional, but
                        // just making sure you know that you can configure
                        // JSON serialization to work seamlessly with whatever
                        // the application on the other end is doing
                    });
                });
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/RabbitMQ/Wolverine.RabbitMQ.Tests/Samples.cs#L193-L226' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_rabbitmq_interop_with_masstransit' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Here's some details that you will need to know:

* While Wolverine *can* send message type information to MassTransit, Wolverine is not (yet) able to glean the message
  type from MassTransit metadata, so you will have to hard code the incoming message type for a particular Wolverine endpoint
  that is receiving messages from a MassTransit application
* Wolverine is able to do request/reply semantics with MassTransit, but there might be hiccups using Wolverine's automatic reply queues just because
  of differing naming conventions or reserved characters leaking through.
* You probably want to use the `RegisterInteropMessageAssembly(Assembly)` for any assemblies of reused DTO message types between
  MassTransit and your Wolverine application to help Wolverine be able to map from NServiceBus publishing by an interface and Wolverine only
  handling concrete types

## Interop with NServiceBus

NServiceBus has a wire protocol that is much more similar to Wolverine and works a little more cleanly -- except for Amazon SQS or SNS that is again, weird.

For the transports that support NServiceBus, opt into the interoperability on an endpoint by endpoint basis with this syntax:

<!-- snippet: sample_opting_into_nservicebus -->
<a id='snippet-sample_opting_into_nservicebus'></a>
```cs
var builder = Host.CreateApplicationBuilder();
builder.UseWolverine(opts =>
{
    // One way or another, you're probably pulling the Azure Service Bus
    // connection string out of configuration
    var azureServiceBusConnectionString = builder
        .Configuration
        .GetConnectionString("azure-service-bus");

    // Connect to the broker in the simplest possible way
    opts.UseAzureServiceBus(azureServiceBusConnectionString).AutoProvision();

    // I overrode the buffering limits just to show
    // that they exist for "back pressure"
    opts.ListenToAzureServiceBusQueue("incoming")
        .UseNServiceBusInterop();
    
    // This facilitates messaging from NServiceBus (or MassTransit) sending as interface
    // types, whereas Wolverine only wants to deal with concrete types
    opts.Policies.RegisterInteropMessageAssembly(typeof(IInterfaceMessage).Assembly);
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Azure/Wolverine.AzureServiceBus.Tests/DocumentationSamples.cs#L509-L533' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_opting_into_nservicebus' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

And some details that you will need to know:

* Wolverine is able to detect the message type from the standard NServiceBus headers. You *might* need to utilize the [message type aliasing](/guide/messages.html#message-type-name-or-alias) to match
  the NServiceBus name for a message type
* You probably want to use the `RegisterInteropMessageAssembly(Assembly)` for any assemblies of reused DTO message types between 
  NServiceBus and your Wolverine application to help Wolverine be able to map from NServiceBus publishing by an interface and Wolverine only
  handling concrete types
* Wolverine does support request/reply interactions with NServiceBus. Wolverine is able to interpret and also translate to NServiceBus's version of Wolverine's `Envelope.ReplyUri`

## Interop with CloudEvents

We're honestly not sure how pervasive the [CloudEvents specification](https://cloudevents.io/) is really used outside of
Microsoft's [Dapr](https://dapr.io/), but there have been enough mentions of this from the Wolverine community to justify its adoption. 

CloudEvents works by publishing messages in its own standardized JSON [envelope wrapper](). The Wolverine to CloudEvents interoperability
is mapping between Wolverine's `Envelope` and the CloudEvents JSON payload, with the actual message data being embedded in
the CloudEvents JSON.

For the transports that support CloudEvents, you need to opt into the CloudEvents interoperability on an endpoint by endpoint
basis like this:

<!-- snippet: sample_rabbitmq_interop_with_cloudevents -->
<a id='snippet-sample_rabbitmq_interop_with_cloudevents'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // *A* way to configure Rabbit MQ using their Uri schema
        // documented here: https://www.rabbitmq.com/uri-spec.html
        opts.UseRabbitMq(new Uri("amqp://localhost"));

        // Set up a listener for a queue
        opts.ListenToRabbitQueue("incoming1")

            // Just note that you *can* override the STJ serialization
            // settings for messages coming in with the CloudEvents
            // wrapper
            .InteropWithCloudEvents(new JsonSerializerOptions());
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/RabbitMQ/Wolverine.RabbitMQ.Tests/Samples.cs#L231-L249' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_rabbitmq_interop_with_cloudevents' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

With CloudEvents interoperability:

* Basic correlation and causation is mapped for Open Telemetry style traceability
* Wolverine is again depending on [message type aliases](/guide/messages.html#message-type-name-or-alias) to "know" what message type the CloudEvents envelopes are referring to, and you might very well
  have to explicitly register message type aliases to bridge the gap between CloudEvents and your Wolverine application.

