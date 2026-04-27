using Shouldly;
using Xunit;

namespace Wolverine.Grpc.Tests;

public class broker_role_tests
{
    [Fact]
    public void grpc_endpoint_broker_role_is_grpc()
    {
        new GrpcEndpoint(new Uri("grpc://localhost:5001")).BrokerRole.ShouldBe("grpc");
    }
}
