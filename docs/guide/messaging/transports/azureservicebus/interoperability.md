# Interoperability

::: tip
Also see the more generic [Wolverine Guide on Interoperability](/tutorials/interop)
:::

Hey, it's a complicated world and Wolverine is a relative newcomer, so it's somewhat likely you'll find yourself needing to make a Wolverine application talk via Azure Service Bus to
a non-Wolverine application. Not to worry (too much), Wolverine has you covered with the ability to customize Wolverine to Azure Service Bus mapping.

You can create interoperability with non-Wolverine applications by writing a custom `IAzureServiceBusEnvelopeMapper`
as shown in the following sample:

<!-- snippet: sample_custom_azure_service_bus_mapper -->
<a id='snippet-sample_custom_azure_service_bus_mapper'></a>
```cs
public class CustomAzureServiceBusMapper : IAzureServiceBusEnvelopeMapper
{
    public void MapEnvelopeToOutgoing(Envelope envelope, ServiceBusMessage outgoing)
    {
        outgoing.Body = new BinaryData(envelope.Data!);
        if (envelope.DeliverWithin != null)
        {
            outgoing.TimeToLive = envelope.DeliverWithin.Value;
        }
    }

    public void MapIncomingToEnvelope(Envelope envelope, ServiceBusReceivedMessage incoming)
    {
        envelope.Data = incoming.Body.ToArray();

        // You will have to help Wolverine out by either telling Wolverine
        // what the message type is, or by reading the actual message object,
        // or by telling Wolverine separately what the default message type
        // is for a listening endpoint
        envelope.MessageType = typeof(Message1).ToMessageTypeName();
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Azure/Wolverine.AzureServiceBus.Tests/Samples.cs#L194-L218' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_custom_azure_service_bus_mapper' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

To apply that mapper to specific endpoints, use this syntax on any type of Azure Service Bus endpoint:

<!-- snippet: sample_configuring_custom_envelope_mapper_for_azure_service_bus -->
<a id='snippet-sample_configuring_custom_envelope_mapper_for_azure_service_bus'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UseAzureServiceBus("some connection string")
            .UseConventionalRouting()

            .ConfigureListeners(l => l.InteropWith(new CustomAzureServiceBusMapper()))

            .ConfigureSenders(s => s.InteropWith(new CustomAzureServiceBusMapper()));
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Azure/Wolverine.AzureServiceBus.Tests/Samples.cs#L111-L123' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_configuring_custom_envelope_mapper_for_azure_service_bus' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Interoperability with NServiceBus and MassTransit

Azure Service Bus has built in recipes for exchanging messages with NServiceBus and MassTransit applications. Opt into
NServiceBus interoperability endpoint by endpoint:

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
        .GetConnectionString("azure-service-bus")!;

    // Connect to the broker in the simplest possible way
    opts.UseAzureServiceBus(azureServiceBusConnectionString).AutoProvision();

    opts.ListenToAzureServiceBusQueue("incoming")
        .UseNServiceBusInterop();
    
    // This facilitates messaging from NServiceBus (or MassTransit) sending as interface
    // types, whereas Wolverine only wants to deal with concrete types
    opts.Policies.RegisterInteropMessageAssembly(typeof(IInterfaceMessage).Assembly);
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Azure/Wolverine.AzureServiceBus.Tests/DocumentationSamples.cs#L807-L828' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_opting_into_nservicebus' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

MassTransit works the same way with `UseMassTransitInterop()`:

```cs
opts.PublishMessage<OrderShipped>().ToAzureServiceBusQueue("masstransit-orders")
    .UseMassTransitInterop();

opts.ListenToAzureServiceBusQueue("from-masstransit")
    // Use System.Text.Json for the message inside the MassTransit envelope
    .UseMassTransitInterop(mt => mt.UseSystemTextJsonForSerialization())
    .DefaultIncomingMessage<OrderShipped>();
```

`UseNServiceBusInterop()` and `UseMassTransitInterop()` are available on queue and topic subscriber endpoints and on
queue and subscription listener endpoints. See [Interop with NServiceBus](/tutorials/interop#interop-with-nservicebus)
and [Interop with MassTransit](/tutorials/interop#interop-with-masstransit) for the details and caveats.
