using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Transports;

namespace Wolverine.AzureServiceBus.Internal;

public abstract class AzureServiceBusEndpoint : Endpoint, IBrokerEndpoint
{
    public AzureServiceBusTransport Parent { get; }

    public AzureServiceBusEndpoint(AzureServiceBusTransport parent, Uri uri, EndpointRole role) : base(uri, role)
    {
        Parent = parent;
    }

    public ValueTask<bool> CheckAsync()
    {
        throw new NotImplementedException();
    }

    public ValueTask TeardownAsync(ILogger logger)
    {
        throw new NotImplementedException();
    }

    public ValueTask SetupAsync(ILogger logger)
    {
        throw new NotImplementedException();
    }
}