using Shouldly;
using Wolverine.Configuration;
using Wolverine.Transports.Local;
using Wolverine.Transports.SharedMemory;
using Wolverine.Transports.Stub;
using Wolverine.Transports.Tcp;
using Xunit;

namespace CoreTests.Configuration;

/// <summary>
/// Locks down GH-2601: every endpoint kind exposes a short, non-empty
/// <see cref="Endpoint.BrokerRole"/> string identifying the underlying
/// broker object kind ("queue", "exchange", "topic", "subscription",
/// "stream", etc.). CritterWatch and other diagnostic UIs read this.
/// </summary>
public class broker_role_tests
{
    [Fact]
    public void base_default_when_subclass_does_not_set_broker_role()
    {
        // The base Endpoint default — should be a sentinel value, not empty,
        // so a custom subclass that forgets to set BrokerRole still produces
        // something a UI can render.
        new TestEndpoint(EndpointRole.Application).BrokerRole.ShouldBe("endpoint");
    }

    [Theory]
    [MemberData(nameof(CoreEndpoints))]
    public void core_endpoint_has_expected_broker_role(Endpoint endpoint, string expectedRole)
    {
        endpoint.BrokerRole.ShouldBe(expectedRole);
    }

    public static TheoryData<Endpoint, string> CoreEndpoints()
    {
        var stubTransport = new StubTransport();
        var sharedTopic = new SharedMemoryTopic("topic-x");

        return new TheoryData<Endpoint, string>
        {
            { new LocalQueue("queue-x"), "queue" },
            { new StubEndpoint("stub-x", stubTransport), "stub" },
            { new TcpEndpoint("localhost", 2222), "socket" },
            { sharedTopic, "topic" },
            { new SharedMemorySubscription(sharedTopic, "sub-x", EndpointRole.Application), "subscription" },
        };
    }
}
