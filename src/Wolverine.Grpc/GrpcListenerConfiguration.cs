using Wolverine.Configuration;

namespace Wolverine.Grpc;

public class GrpcListenerConfiguration : ListenerConfiguration<GrpcListenerConfiguration, GrpcEndpoint>
{
    public GrpcListenerConfiguration(GrpcEndpoint endpoint) : base(endpoint)
    {
    }
}
