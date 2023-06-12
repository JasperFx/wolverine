# Interoperability with MassTransit

Wolverine is the new kid on the block, and it's quite likely that many folks will already be using MassTransit for messaging.
Fortunately, Wolverine has some ability to exchange messages with MassTransit applications, so both tools can live and
work together.

At this point, the interoperability is only built and tested for the [Rabbit MQ transport](./transports/rabbitmq.md).

Here's a sample:

<!-- snippet: sample_MassTransit_interoperability -->
<a id='snippet-sample_masstransit_interoperability'></a>
```cs
Wolverine = await Host.CreateDefaultBuilder().UseWolverine(opts =>
{
    opts.ApplicationAssembly = GetType().Assembly;

    opts.UseRabbitMq()
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/RabbitMQ/InteropTests/MassTransit/MassTransitSpecs.cs#L21-L48' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_masstransit_interoperability' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

MORE SOON!
