using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Transports;

namespace Wolverine.Pubsub.Internal;

public abstract class PubsubEndpoint : Endpoint, IBrokerEndpoint {
    public PubsubTransport Transport { get; }

    /// <summary>
    /// If specified, applies a custom envelope mapper to this endp[oint
    /// </summary>
    public IPubsubEnvelopeMapper? Mapper { get; set; } = null;

    public PubsubEndpoint(
        Uri uri,
        PubsubTransport transport,
        EndpointRole role = EndpointRole.Application
    ) : base(uri, role) {
        Transport = transport;
    }


    public abstract ValueTask<bool> CheckAsync();
    public abstract ValueTask TeardownAsync(ILogger logger);
    public abstract ValueTask SetupAsync(ILogger logger);

    protected override bool supportsMode(EndpointMode mode) => true;

    internal IPubsubEnvelopeMapper BuildMapper() {
        if (Mapper is not null) return Mapper;

        var mapper = new PubsubEnvelopeMapper(this);

        // Important for interoperability
        if (MessageType is not null) mapper.ReceivesMessage(MessageType);

        return mapper;
    }
}
