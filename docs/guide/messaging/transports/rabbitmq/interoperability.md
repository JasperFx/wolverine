# Interoperability

Hey, it's a complicated world and Wolverine is a relative newcomer, so it's somewhat likely you'll find yourself needing to make a Wolverine application talk via Rabbit MQ to 
a non-Wolverine application. Not to worry (too much), Wolverine has you covered with the ability to customize Wolverine to Rabbit MQ mapping and some built in recipes for 
interoperability with commonly used .NET messaging frameworks.

## Receiving Raw Data

::: tip
Wolverine will be able to publish JSON to non-Wolverine applications out of the box with no further configuration
:::

A lot of Wolverine functionality (request/reply, message correlation) relies on message metadata sent through
Rabbit MQ headers. Sometimes though, you'll simply need Wolverine to receive data from external systems that
certainly aren't speaking Wolverine's header protocol. In the simplest common scenario, you need Wolverine to
be able to process JSON data (JSON is Wolverine's default data format) being published from another system.

If you can make the assumption that Wolverine will only be receiving one type of message at a particular queue, and that
the data will be valid JSON that can be deserialized to that single message type, you can simply tell
Wolverine what the default message type is for that queue like this:

<!-- snippet: sample_setting_default_message_type_with_rabbit -->
<a id='snippet-sample_setting_default_message_type_with_rabbit'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine((context, opts) =>
    {
        var rabbitMqConnectionString = context.Configuration.GetConnectionString("rabbit");

        opts.UseRabbitMq(rabbitMqConnectionString);

        opts.ListenToRabbitQueue("emails")
            // Tell Wolverine to assume that all messages
            // received at this queue are the SendEmail
            // message type
            .DefaultIncomingMessage<SendEmail>();
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/RabbitMQ/Wolverine.RabbitMQ.Tests/Samples.cs#L434-L450' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_setting_default_message_type_with_rabbit' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

With this setting, there is **no other required headers** for Wolverine to process incoming messages. However, Wolverine will be
unable to send responses back to the sender and may have a limited ability to create correlated tracking between
the upstream non-Wolverine system and your Wolverine system.


## Roll Your Own Interoperability

For interoperability, Wolverine needs to map data elements from the Rabbit MQ client `IBasicProperties` model to 
Wolverine's internal `Envelope` model. If you want a more advanced interoperability model that actually tries
to map message metadata, you can implement Wolverine's `IRabbitMqEnvelopeMapper` as shown in this sample:

<!-- snippet: sample_rabbit_special_mapper -->
<a id='snippet-sample_rabbit_special_mapper'></a>
```cs
public class SpecialMapper : IRabbitMqEnvelopeMapper
{
    public void MapEnvelopeToOutgoing(Envelope envelope, IBasicProperties outgoing)
    {
        // All of this is default behavior, but this sample does show
        // what's possible here
        outgoing.CorrelationId = envelope.CorrelationId;
        outgoing.MessageId = envelope.Id.ToString();
        outgoing.ContentType = "application/json";
        
        if (envelope.DeliverBy.HasValue)
        {
            var ttl = Convert.ToInt32(envelope.DeliverBy.Value.Subtract(DateTimeOffset.Now).TotalMilliseconds);
            outgoing.Expiration = ttl.ToString();
        }

        if (envelope.TenantId.IsNotEmpty())
        {
            outgoing.Headers ??= new Dictionary<string, object>();
            outgoing.Headers["tenant-id"] = envelope.TenantId;
        }
    }

    public void MapIncomingToEnvelope(Envelope envelope, IBasicProperties incoming)
    {
        envelope.CorrelationId = incoming.CorrelationId;
        envelope.ContentType = "application/json";
        if (Guid.TryParse(incoming.MessageId, out var id))
        {
            envelope.Id = id;
        }
        else
        {
            envelope.Id = Guid.NewGuid();
        }

        if (incoming.Headers != null && incoming.Headers.TryGetValue("tenant-id", out var tenantId))
        {
            // Watch this in real life, some systems will send header values as
            // byte arrays
            envelope.TenantId = (string)tenantId;
        }
    }

    public IEnumerable<string> AllHeaders()
    {
        yield break;
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/RabbitMQ/Wolverine.RabbitMQ.Tests/SpecialMapper.cs#L7-L59' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_rabbit_special_mapper' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

And register that special mapper like this:

<!-- snippet: sample_registering_custom_rabbit_mq_envelope_mapper -->
<a id='snippet-sample_registering_custom_rabbit_mq_envelope_mapper'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine((context, opts) =>
    {
        var rabbitMqConnectionString = context.Configuration.GetConnectionString("rabbit");

        opts.UseRabbitMq(rabbitMqConnectionString);

        opts.ListenToRabbitQueue("emails")
            // Apply your custom interoperability strategy here
            .UseInterop(new SpecialMapper())
            
            // You may still want to define the default incoming
            // message as the message type name may not be sent
            // by the upstream system
            .DefaultIncomingMessage<SendEmail>();
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/RabbitMQ/Wolverine.RabbitMQ.Tests/Samples.cs#L455-L474' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_registering_custom_rabbit_mq_envelope_mapper' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->


## Interoperability with NServiceBus

::: warning
You may need to override Wolverine's Rabbit MQ dead letter queue settings to avoid Wolverine and NServiceBus declaring queues
with different settings and stomping all over each other. The Wolverine team blames NServiceBus for this one:-)
:::

Wolverine is the new kid on the block, and it's quite likely that many folks will already be using NServiceBus for messaging.
Fortunately, Wolverine has some ability to exchange messages with NServiceBus applications, so both tools can live and
work together.

At this point, the interoperability is only built and tested for the [Rabbit MQ transport](./transports/rabbitmq.md).

Here's a sample:

<!-- snippet: sample_NServiceBus_interoperability -->
<a id='snippet-sample_nservicebus_interoperability'></a>
```cs
Wolverine = await Host.CreateDefaultBuilder().UseWolverine(opts =>
{
    opts.UseRabbitMq()
        .AutoProvision().AutoPurgeOnStartup()
        .BindExchange("wolverine").ToQueue("wolverine")
        .BindExchange("nsb").ToQueue("nsb")
        .BindExchange("NServiceBusRabbitMqService:ResponseMessage").ToQueue("wolverine");

    opts.PublishAllMessages().ToRabbitExchange("nsb")

        // Tell Wolverine to make this endpoint send messages out in a format
        // for NServiceBus
        .UseNServiceBusInterop();

    opts.ListenToRabbitQueue("wolverine")
        .UseNServiceBusInterop()
        

        .UseForReplies();
    
    // This facilitates messaging from NServiceBus (or MassTransit) sending as interface
    // types, whereas Wolverine only wants to deal with concrete types
    opts.Policies.RegisterInteropMessageAssembly(typeof(IInterfaceMessage).Assembly);
}).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/RabbitMQ/InteropTests/NServiceBus/NServiceBusFixture.cs#L16-L43' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_nservicebus_interoperability' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Interoperability with Mass Transit

Wolverine can interoperate bi-directionally with [MassTransit](https://masstransit-project.com/) using [RabbitMQ](http://www.rabbitmq.com/).
At this point, the interoperability is **only** functional if MassTransit is using its standard "envelope" serialization
approach (i.e., **not** using raw JSON serialization).

::: warning
At this point, if an endpoint is set up for interoperability with MassTransit, reserve that endpoint for traffic
with MassTransit, and don't try to use that endpoint for Wolverine to Wolverine traffic
:::

The configuration to do this is shown below:

<!-- snippet: sample_MassTransit_interoperability -->
<a id='snippet-sample_masstransit_interoperability'></a>
```cs
Wolverine = await Host.CreateDefaultBuilder().UseWolverine(opts =>
{
    opts.ApplicationAssembly = GetType().Assembly;

    opts.UseRabbitMq()
        .CustomizeDeadLetterQueueing(new DeadLetterQueue("errors", DeadLetterQueueMode.InteropFriendly))
        .AutoProvision().AutoPurgeOnStartup()
        .BindExchange("wolverine").ToQueue("wolverine")
        .BindExchange("masstransit").ToQueue("masstransit");

    opts.PublishAllMessages().ToRabbitExchange("masstransit")

        // Tell Wolverine to make this endpoint send messages out in a format
        // for MassTransit
        .UseMassTransitInterop();

    opts.ListenToRabbitQueue("wolverine")

        // Tell Wolverine to make this endpoint interoperable with MassTransit
        .UseMassTransitInterop(mt =>
        {
            // optionally customize the inner JSON serialization
        })
        .DefaultIncomingMessage<ResponseMessage>().UseForReplies();
}).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/RabbitMQ/InteropTests/MassTransit/MassTransitSpecs.cs#L21-L49' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_masstransit_interoperability' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->
