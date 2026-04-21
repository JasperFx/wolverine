using Wolverine.Configuration;

namespace Wolverine.Grpc;

public class GrpcSubscriberConfiguration : SubscriberConfiguration<GrpcSubscriberConfiguration, GrpcEndpoint>
{
    public GrpcSubscriberConfiguration(GrpcEndpoint endpoint) : base(endpoint)
    {
    }
}
