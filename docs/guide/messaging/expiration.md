# Message Expiration

Some messages you publish or send will be transient, or only be valid for only a brief time. In this case
you may find it valuable to apply message expiration rules to tell Wolverine to ignore messages that are
too old.

You won't use this explicitly very often, but this information is ultimately stored on the Wolverine `Envelope` with
this property:

<!-- snippet: sample_envelope_deliver_by_property -->
<a id='snippet-sample_envelope_deliver_by_property'></a>
```cs
/// <summary>
///     Instruct Wolverine to throw away this message if it is not successfully sent and processed
///     by the time specified
/// </summary>
public DateTimeOffset? DeliverBy
{
    get => _deliverBy;
    set => _deliverBy = value?.ToUniversalTime();
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Wolverine/Envelope.cs#L54-L66' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_envelope_deliver_by_property' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

At runtime, Wolverine will:

1. Wolverine will discard received messages that are past their `DeliverBy` time 
2. Wolverine will also discard outgoing messages that are past their `DeliverBy` time
3. For transports that support this (Rabbit MQ for example), Wolverine will try to pass the `DeliverBy` time into a transport's native message expiration capabilities

## At Message Sending Time

On a message by message basis, you can explicitly set the deliver by time either as an absolute time or as a `TimeSpan` past now
with this syntax:

<!-- snippet: sample_message_expiration_by_message -->
<a id='snippet-sample_message_expiration_by_message'></a>
```cs
public async Task message_expiration(IMessageBus bus)
{
    // Disregard the message if it isn't sent and/or processed within 3 seconds from now
    await bus.SendAsync(new StatusUpdate("Okay"), new DeliveryOptions { DeliverWithin = 3.Seconds() });

    // Disregard the message if it isn't sent and/or processed by 3 PM today
    // but watch all the potentially harmful time zone issues in your real code that I'm ignoring here!
    await bus.SendAsync(new StatusUpdate("Okay"), new DeliveryOptions { DeliverBy = DateTime.Today.AddHours(15) });
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Azure/Wolverine.AzureServiceBus.Tests/DocumentationSamples.cs#L447-L459' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_message_expiration_by_message' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## By Subscriber

The message expiration can be set as a rule for all messages sent to a specific subscribing endpoint as shown by
this sample:

<!-- snippet: sample_delivery_expiration_rules_per_subscriber -->
<a id='snippet-sample_delivery_expiration_rules_per_subscriber'></a>
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

    // Explicitly configure a delivery expiration of 5 seconds
    // for a specific Azure Service Bus queue
    opts.PublishMessage<StatusUpdate>().ToAzureServiceBusQueue("transient")

        // If the messages are transient, it's likely that they should not be
        // durably stored, so make things lighter in your system
        .BufferedInMemory()
        .DeliverWithin(5.Seconds());
});

using var host = builder.Build();
await host.StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Azure/Wolverine.AzureServiceBus.Tests/DocumentationSamples.cs#L284-L311' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_delivery_expiration_rules_per_subscriber' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## By Message Type

At the message type level, you can set message expiration rules with the `Wolverine.Attributes.DeliverWithinAttribute` attribute
on the message type as in this sample:

<!-- snippet: sample_using_deliver_within_attribute -->
<a id='snippet-sample_using_deliver_within_attribute'></a>
```cs
// The attribute directs Wolverine to send this message with
// a "deliver within 5 seconds, or discard" directive
[DeliverWithin(5)]
public record AccountUpdated(Guid AccountId, decimal Balance);
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/Middleware/AppWithMiddleware/Account.cs#L164-L171' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_deliver_within_attribute' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->
