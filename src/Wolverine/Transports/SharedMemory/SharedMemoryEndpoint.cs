using Wolverine.Configuration;

namespace Wolverine.Transports.SharedMemory;

public abstract class SharedMemoryEndpoint : Endpoint
{
    protected SharedMemoryEndpoint(Uri uri, EndpointRole role) : base(uri, role)
    {
    }
}