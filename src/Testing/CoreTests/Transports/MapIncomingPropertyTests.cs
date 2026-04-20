using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using Xunit;

namespace CoreTests.Transports;

public class MapIncomingPropertyTests
{
    [Fact]
    public void uses_the_custom_incoming_mapping()
    {
        var expectedReplyUri = new Uri("stub://test:123/");
        var mapper = new StubEnvelopeMapper(new StubEndpoint());
        mapper.MapIncomingProperty(x => x.ReplyUri!, (envelope, incoming) =>
        {
            envelope.ReplyUri = new Uri(incoming.SpecialReply!);
        });

        var envelope = new Envelope();
        mapper.MapIncomingToEnvelope(envelope, new StubTransportMessage
        {
            SpecialReply = expectedReplyUri.ToString()
        });

        envelope.ReplyUri.ShouldBe(expectedReplyUri);
    }
}

internal class StubTransportMessage
{
    public Dictionary<string, string?> Headers { get; } = new();
    public string? SpecialReply { get; set; }
}

internal class StubEnvelopeMapper(Endpoint endpoint)
    : EnvelopeMapper<StubTransportMessage, StubTransportMessage>(endpoint)
{
    protected override void writeOutgoingHeader(StubTransportMessage outgoing, string key, string value)
    {
        outgoing.Headers[key] = value;
    }

    protected override bool tryReadIncomingHeader(StubTransportMessage incoming, string key, out string? value)
    {
        return incoming.Headers.TryGetValue(key, out value);
    }
}

internal class StubEndpoint() : Endpoint(new Uri("stub://mapper"), EndpointRole.Application)
{
    public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
    {
        throw new NotImplementedException();
    }

    protected override ISender CreateSender(IWolverineRuntime runtime)
    {
        throw new NotImplementedException();
    }
}
