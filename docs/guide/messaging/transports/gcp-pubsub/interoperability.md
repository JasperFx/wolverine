# Interoperability

Hey, it's a complicated world and Wolverine is a relative newcomer, so it's somewhat likely you'll find yourself needing to make a Wolverine application talk via GCP Pub/Sub to
a non-Wolverine application. Not to worry (too much), Wolverine has you covered with the ability to customize Wolverine to GCP Pub/Sub mapping.

You can create interoperability with non-Wolverine applications by writing a custom `IPubsubEnvelopeMapper`
as shown in the following sample:

<!-- snippet: sample_custom_pubsub_mapper -->
<a id='snippet-sample_custom_pubsub_mapper'></a>
```cs
public class CustomPubsubMapper : EnvelopeMapper<ReceivedMessage, PubsubMessage>, IPubsubEnvelopeMapper
{
    public CustomPubsubMapper(PubsubEndpoint endpoint) : base(endpoint) { }

    public void MapIncomingToEnvelope(PubsubEnvelope envelope, ReceivedMessage incoming)
    {
        envelope.AckId = incoming.AckId;

        // You will have to help Wolverine out by either telling Wolverine
        // what the message type is, or by reading the actual message object,
        // or by telling Wolverine separately what the default message type
        // is for a listening endpoint
        envelope.MessageType = typeof(Message1).ToMessageTypeName();

    }

    public void MapOutgoingToMessage(OutgoingMessageBatch outgoing, PubsubMessage message)
    {
        message.Data = ByteString.CopyFrom(outgoing.Data);
    }

    protected override void writeOutgoingHeader(PubsubMessage outgoing, string key, string value)
    {
        outgoing.Attributes[key] = value;
    }

    protected override void writeIncomingHeaders(ReceivedMessage incoming, Envelope envelope)
    {
        if (incoming.Message.Attributes is null) return;

        foreach (var pair in incoming.Message.Attributes) envelope.Headers[pair.Key] = pair.Value?.ToString();
    }

    protected override bool tryReadIncomingHeader(ReceivedMessage incoming, string key, out string? value)
    {
        if (incoming.Message.Attributes.TryGetValue(key, out var header))
        {
            value = header.ToString();

            return true;
        }

        value = null;

        return false;
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/GCP/Wolverine.Pubsub.Tests/DocumentationSamples.cs#L253-L299' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_custom_pubsub_mapper' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

To apply that mapper to specific endpoints, use this syntax on any type of GCP Pub/Sub endpoint:

<!-- snippet: sample_configuring_custom_envelope_mapper_for_pubsub -->
<a id='snippet-sample_configuring_custom_envelope_mapper_for_pubsub'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UsePubsub("your-project-id")
            .UseConventionalRouting()
            .ConfigureListeners(l => l.InteropWith(e => new CustomPubsubMapper(e)))
            .ConfigureSenders(s => s.InteropWith(e => new CustomPubsubMapper(e)));
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/GCP/Wolverine.Pubsub.Tests/DocumentationSamples.cs#L238-L245' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_configuring_custom_envelope_mapper_for_pubsub' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->
