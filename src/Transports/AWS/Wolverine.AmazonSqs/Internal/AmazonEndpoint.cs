using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Transports;

namespace Wolverine.AmazonSqs.Internal;

public abstract class AmazonEndpoint : Endpoint, IBrokerQueue
{
    protected readonly AmazonSqsTransport Parent;
    
    protected AmazonEndpoint(string endpointName, AmazonSqsTransport parent, Uri uri) : base(uri, EndpointRole.Application)
    {
        Parent = parent;
        EndpointName = endpointName;
    }
    
    public abstract ValueTask<bool> CheckAsync();
    public abstract ValueTask TeardownAsync(ILogger logger);
    public abstract ValueTask SetupAsync(ILogger logger);
    public abstract ValueTask PurgeAsync(ILogger logger);
    public abstract ValueTask<Dictionary<string, string>> GetAttributesAsync();

    internal abstract Task SendMessageAsync(Envelope envelope, ILogger logger);
}
