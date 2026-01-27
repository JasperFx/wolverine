# Session Identifiers and FIFO Queues

::: info
This functionality was introduced in Wolverine 1.6.0.
:::

::: warning
Even if Wolverine isn't controlling the creation of the queues or subscriptions, you still need to tell
Wolverine when sessions are required on any listening endpoint so that it can opt into session compliant listeners
:::

You can now take advantage of [sessions and first-in, first out queues in Azure Service Bus](https://learn.microsoft.com/en-us/azure/service-bus-messaging/message-sessions) with Wolverine. 
To tell Wolverine that an Azure Service Bus queue or subscription should require sessions, you have this syntax shown in an internal test:

<!-- snippet: sample_using_azure_service_bus_session_identifiers -->
<a id='snippet-sample_using_azure_service_bus_session_identifiers'></a>
```cs
_host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UseAzureServiceBusTesting()
            .AutoProvision().AutoPurgeOnStartup();

        opts.ListenToAzureServiceBusQueue("send_and_receive");
        opts.PublishMessage<AsbMessage1>().ToAzureServiceBusQueue("send_and_receive");

        opts.ListenToAzureServiceBusQueue("fifo1")

            // Require session identifiers with this queue
            .RequireSessions()

            // This controls the Wolverine handling to force it to process
            // messages sequentially
            .Sequential();

        opts.PublishMessage<AsbMessage2>()
            .ToAzureServiceBusQueue("fifo1");

        opts.PublishMessage<AsbMessage3>().ToAzureServiceBusTopic("asb3").SendInline();
        opts.ListenToAzureServiceBusSubscription("asb3")
            .FromTopic("asb3")

            // Require sessions on this subscription
            .RequireSessions(1)

            .ProcessInline();

        opts.PublishMessage<AsbMessage4>().ToAzureServiceBusTopic("asb4").BufferedInMemory();
        opts.ListenToAzureServiceBusSubscription("asb4")
            .FromTopic("asb4", cfg =>
            {
                cfg.EnablePartitioning = true;
            })

            // Require sessions on this subscription
            .RequireSessions(1)

            .ProcessInline();
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Azure/Wolverine.AzureServiceBus.Tests/end_to_end.cs#L18-L63' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_azure_service_bus_session_identifiers' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

To publish messages to Azure Service Bus with a session id, you will need to of course supply the session id:

<!-- snippet: sample_sending_with_session_identifier -->
<a id='snippet-sample_sending_with_session_identifier'></a>
```cs
// bus is an IMessageBus
await bus.SendAsync(new AsbMessage3("Red"), new DeliveryOptions { GroupId = "2" });
await bus.SendAsync(new AsbMessage3("Green"), new DeliveryOptions { GroupId = "2" });
await bus.SendAsync(new AsbMessage3("Refactor"), new DeliveryOptions { GroupId = "2" });
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Azure/Wolverine.AzureServiceBus.Tests/end_to_end.cs#L153-L160' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_sending_with_session_identifier' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

::: info
Wolverine is using the "group-id" nomenclature from the AMPQ standard, but for Azure Service Bus, this is directly
mapped to the `SessionId` property on the Azure Service Bus client internally.
:::

You can also send messages with session identifiers through cascading messages as shown in a fake message handler
below:

<!-- snippet: sample_group_id_and_cascading_messages -->
<a id='snippet-sample_group_id_and_cascading_messages'></a>
```cs
public static IEnumerable<object> Handle(IncomingMessage message)
{
    yield return new Message1().WithGroupId("one");
    yield return new Message2().WithGroupId("one");

    yield return new Message3().ScheduleToGroup("one", 5.Minutes());

    // Long hand
    yield return new Message4().WithDeliveryOptions(new() { GroupId = "one" });
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/using_group_ids.cs#L9-L22' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_group_id_and_cascading_messages' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

