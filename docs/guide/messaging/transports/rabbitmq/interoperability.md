# Interoperability

Hey, it's a complicated world and Wolverine is a relative newcomer, so it's somewhat likely you'll find yourself needing to make a Wolverine application talk via Rabbit MQ to 
a non-Wolverine application. Not to worry (too much), Wolverine has you covered with the ability to customize Wolverine to Rabbit MQ mapping and some built in recipes for 
interoperability with commonly used .NET messaging frameworks.

## Connecting to non-Wolverine Applications

TODO - content!

## Interoperability with NServiceBus

TODO - content!

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
