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
        
        //.DefaultIncomingMessage<ResponseMessage>()
        
        .UseForReplies();
    
    // This facilitates messaging from NServiceBus (or MassTransit) sending as interface
    // types, whereas Wolverine only wants to deal with concrete types
    opts.Policies.RegisterInteropMessageAssembly(typeof(IInterfaceMessage).Assembly);
}).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Wolverine.RabbitMQ.Tests/Interop/NServiceBus/NServiceBusFixture.cs#L18-L46' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_nservicebus_interoperability' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

MORE SOON!
