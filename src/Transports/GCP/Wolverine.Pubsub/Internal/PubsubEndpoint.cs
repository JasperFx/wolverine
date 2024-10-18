using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Transports;

namespace Wolverine.Pubsub.Internal;

public abstract class PubsubEndpoint : Endpoint, IBrokerEndpoint {
    private IPubsubEnvelopeMapper? _mapper;
    protected readonly PubsubTransport _transport;

    protected bool _hasInitialized = false;

    /// <summary>
    ///     Pluggable strategy for interoperability with non-Wolverine systems. Customizes how the incoming Google Cloud Pub/Sub messages
    ///     are read and how outgoing messages are written to Google Cloud Pub/Sub.
    /// </summary>
    public IPubsubEnvelopeMapper Mapper {
        get {
            if (_mapper is not null) return _mapper;

            var mapper = new PubsubEnvelopeMapper(this);

            // Important for interoperability
            if (MessageType != null) mapper.ReceivesMessage(MessageType);

            _mapper = mapper;

            return _mapper;
        }
        set => _mapper = value;
    }

    public PubsubEndpoint(
        Uri uri,
        PubsubTransport transport,
        EndpointRole role = EndpointRole.Application
    ) : base(uri, role) {
        _transport = transport;
    }

    public override async ValueTask InitializeAsync(ILogger logger) {
        if (_hasInitialized) return;

        try {
            if (_transport.AutoProvision) await SetupAsync(logger);
        }
        catch (Exception ex) {
            throw new WolverinePubsubTransportException($"{Uri}: Error trying to initialize Google Cloud Pub/Sub endpoint", ex);
        }

        _hasInitialized = true;
    }

    public abstract ValueTask SetupAsync(ILogger logger);
    public abstract ValueTask<bool> CheckAsync();
    public abstract ValueTask TeardownAsync(ILogger logger);

    protected override bool supportsMode(EndpointMode mode) => true;
}
