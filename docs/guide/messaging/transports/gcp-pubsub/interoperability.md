# Interoperability

::: tip
Also see the more generic [Wolverine Guide on Interoperability](/tutorials/interop)
:::

Hey, it's a complicated world and Wolverine is a relative newcomer, so it's somewhat likely you'll find yourself needing to make a Wolverine application talk via GCP Pub/Sub to
a non-Wolverine application. Not to worry (too much), Wolverine has you covered with the ability to customize Wolverine to GCP Pub/Sub mapping.

You can create interoperability with non-Wolverine applications by writing a custom `IPubsubEnvelopeMapper`
as shown in the following sample:

<!-- snippet: sample_custom_pubsub_mapper -->
<a id='snippet-sample_custom_pubsub_mapper'></a>
```cs
public class CustomPubsubMapper : EnvelopeMapper<PubsubMessage, PubsubMessage>, IPubsubEnvelopeMapper
{
    public CustomPubsubMapper(PubsubEndpoint endpoint) : base(endpoint)
    {
    }

    public void MapOutgoingToMessage(OutgoingMessageBatch outgoing, PubsubMessage message)
    {
        message.Data = ByteString.CopyFrom(outgoing.Data);
    }

    protected override void writeOutgoingHeader(PubsubMessage outgoing, string key, string value)
    {
        outgoing.Attributes[key] = value;
    }

    protected override void writeIncomingHeaders(PubsubMessage incoming, Envelope envelope)
    {
        if (incoming.Attributes is null)
        {
            return;
        }

        foreach (var pair in incoming.Attributes) envelope.Headers[pair.Key] = pair.Value;
    }

    protected override bool tryReadIncomingHeader(PubsubMessage incoming, string key, out string? value)
    {
        if (incoming.Attributes.TryGetValue(key, out var header))
        {
            value = header;

            return true;
        }

        value = null;

        return false;
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/GCP/Wolverine.Pubsub.Tests/DocumentationSamples.cs#L245-L288' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_custom_pubsub_mapper' title='Start of snippet'>anchor</a></sup>
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
            .ConfigureListeners(l => l.UseInterop((e, _) => new CustomPubsubMapper(e)))
            .ConfigureSenders(s => s.UseInterop((e, _) => new CustomPubsubMapper(e)));
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/GCP/Wolverine.Pubsub.Tests/DocumentationSamples.cs#L230-L241' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_configuring_custom_envelope_mapper_for_pubsub' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->
