using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.Pubsub.Internal;

public abstract class PubsubEndpoint : Endpoint<IPubsubEnvelopeMapper, PubsubEnvelopeMapper>, IBrokerEndpoint
{
    private readonly PubsubTransport _parent;

    protected PubsubEndpoint(PubsubTransport parent, Uri uri, EndpointRole role) : base(uri, role)
    {
        _parent = parent;
    }

    public override ValueTask InitializeAsync(ILogger logger)
    {
        if (_parent.AutoProvision)
        {
            return SetupAsync(logger);
        }

        return new ValueTask();
    }

    public abstract ValueTask<bool> CheckAsync();

    public abstract ValueTask TeardownAsync(ILogger logger);

    public abstract ValueTask SetupAsync(ILogger logger);

    protected override PubsubEnvelopeMapper buildMapper(IWolverineRuntime runtime)
    {
        return new PubsubEnvelopeMapper(this);
    }
}