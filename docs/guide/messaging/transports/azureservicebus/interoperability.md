# Interoperability

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
        outgoing.Body = new BinaryData(envelope.Data);
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

    public IEnumerable<string> AllHeaders()
    {
        yield break;
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Azure/Wolverine.AzureServiceBus.Tests/Samples.cs#L120-L150' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_custom_azure_service_bus_mapper' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Azure/Wolverine.AzureServiceBus.Tests/Samples.cs#L103-L116' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_configuring_custom_envelope_mapper_for_azure_service_bus' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->
