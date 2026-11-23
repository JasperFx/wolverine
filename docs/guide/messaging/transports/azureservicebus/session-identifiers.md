# Session Identifiers and FIFO Queues

::: info
This functionality was introduced in Wolverine 1.6.0.
:::

::: warning
Even if Wolverine isn't controlling the creation of the queues or subscriptions, you still need to tell
Wolverine when sessions are required on any listening endpoint so that it can opt into session compliant listeners
:::

You can now take advantage of [sessions and first-in, first out queues in Azure Service Bus](https://learn.microsoft.com/en-us/azure/service-bus-messaging/message-sessions) with Wolverine. 
To tell Wolverine that an Azure Service Bus queue or subscription should require sessions, use this syntax (shown here against the
[Azure Service Bus emulator](/guide/messaging/transports/azureservicebus/emulator), but identical against a real namespace):

<!-- snippet: sample_using_azure_service_bus_session_identifiers -->
<a id='snippet-sample_using_azure_service_bus_session_identifiers'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UseAzureServiceBusEmulator()
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
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Azure/Wolverine.AzureServiceBus.Tests/DocumentationSamples.cs#L152-L185' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_azure_service_bus_session_identifiers' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Azure/Wolverine.AzureServiceBus.Tests/end_to_end.cs#L143-L149' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_sending_with_session_identifier' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

::: info
Wolverine is using the "group-id" nomenclature from the AMPQ standard, but for Azure Service Bus, this is directly
mapped to the `SessionId` property on the Azure Service Bus client internally.
:::

## Pinning a Listener to Specific Session Identifiers

::: info
This functionality was introduced in Wolverine 6.22.0.
:::

Sometimes you want several competing consumers to share a *single* queue or subscription, but have each consumer only
ever process messages for a fixed set of session identifiers. Because Azure Service Bus enforces that only one receiver
can hold a session lock at a time, the session id effectively becomes a *broker-enforced routing key*: a consumer pinned
to `"A"` will never see the messages meant for `"B"`, even though both consumers are listening to the exact same entity.

Use `RequireSessionsWithOnlyTheseIdentifiers(...)` to pin a listener:

<!-- snippet: sample_pinning_azure_service_bus_session_identifiers -->
<a id='snippet-sample_pinning_azure_service_bus_session_identifiers'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UseAzureServiceBus("some connection string").AutoProvision();

        // Two competing consumers share ONE queue, but each only ever locks its own
        // session id -- on a shared entity the session id becomes a broker-enforced
        // routing key, so neither consumer ever sees the other's messages.
        opts.ListenToAzureServiceBusQueue("shared-orders")
            .RequireSessionsWithOnlyTheseIdentifiers("A");

        // (running on a different node)
        // opts.ListenToAzureServiceBusQueue("shared-orders")
        //     .RequireSessionsWithOnlyTheseIdentifiers("B");

        opts.PublishMessage<OrderPlaced>().ToAzureServiceBusQueue("shared-orders");
    }).StartAsync();

// The producer selects the target consumer with the session id (GroupId)
var bus = host.MessageBus();
await bus.PublishAsync(new OrderPlaced("1"), new DeliveryOptions { GroupId = "A" }); // only "A" receives
await bus.PublishAsync(new OrderPlaced("2"), new DeliveryOptions { GroupId = "B" }); // only "B" receives
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Azure/Wolverine.AzureServiceBus.Tests/DocumentationSamples.cs#L190-L215' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_pinning_azure_service_bus_session_identifiers' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The producer simply selects the target consumer by setting the session id (the `GroupId`) on the outgoing message, exactly
as with any other session-enabled endpoint.

For any of the other `ServiceBusSessionProcessorOptions` knobs -- `MaxConcurrentSessions`, `MaxAutoLockRenewalDuration`,
`SessionIdleTimeout`, and so on -- use the general `ConfigureSessionProcessor(...)` hook:

<!-- snippet: sample_configuring_azure_service_bus_session_processor -->
<a id='snippet-sample_configuring_azure_service_bus_session_processor'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UseAzureServiceBus("some connection string").AutoProvision();

        // The general hook for any ServiceBusSessionProcessorOptions knob
        opts.ListenToAzureServiceBusQueue("orders")
            .ConfigureSessionProcessor(o =>
            {
                o.MaxConcurrentSessions = 8;
                o.MaxAutoLockRenewalDuration = TimeSpan.FromMinutes(10);
                o.SessionIdleTimeout = TimeSpan.FromSeconds(30);
            });
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Azure/Wolverine.AzureServiceBus.Tests/DocumentationSamples.cs#L220-L237' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_configuring_azure_service_bus_session_processor' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

::: tip
Calling `ConfigureSessionProcessor(...)` or `RequireSessionsWithOnlyTheseIdentifiers(...)` switches the session listener
from Wolverine's default `AcceptNextSession` loop to the Azure SDK's `ServiceBusSessionProcessor`. Session listeners that
don't use either of these methods are completely unaffected. Wolverine reserves control of the `ReceiveMode` and
`AutoCompleteMessages` options that its acknowledgement, deferral, and dead lettering depend on.
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/using_group_ids.cs#L9-L21' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_group_id_and_cascading_messages' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

