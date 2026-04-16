using Shouldly;
using Wolverine.Runtime;
using Xunit;

namespace MetricsTests;

public class is_system_endpoint_tests
{
    [Theory]
    [InlineData("rabbitmq://localhost/wolverine.response.abc123", true)]
    [InlineData("rabbitmq://localhost/wolverine.Response.ABC123", true)]
    [InlineData("redis://localhost/wolverine.response.node1", true)]
    [InlineData("local://replies", true)]
    [InlineData("local://durable", true)]
    [InlineData("rabbitmq://localhost/my-queue", false)]
    [InlineData("tcp://localhost:5000", false)]
    public void should_identify_system_endpoints(string uriString, bool expected)
    {
        var uri = new Uri(uriString);
        WolverineRuntime.IsSystemEndpoint(uri).ShouldBe(expected);
    }

    [Fact]
    public void null_uri_is_not_system_endpoint()
    {
        WolverineRuntime.IsSystemEndpoint(null).ShouldBeFalse();
    }
}
