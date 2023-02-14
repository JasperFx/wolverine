# Interoperability with NServiceBus

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
        .DefaultIncomingMessage<ResponseMessage>().UseForReplies();
}).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Wolverine.RabbitMQ.Tests/Interop/NServiceBus/NServiceBusFixture.cs#L17-L38' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_nservicebus_interoperability' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

MORE SOON!
