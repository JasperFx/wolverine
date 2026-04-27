using Shouldly;
using Wolverine.Configuration;
using Wolverine.Redis.Internal;
using Xunit;

namespace Wolverine.Redis.Tests;

public class broker_role_tests
{
    [Fact]
    public void redis_stream_endpoint_broker_role_is_stream()
    {
        var transport = new RedisTransport();
        new RedisStreamEndpoint(new Uri("redis://stream/0/sample"), transport, EndpointRole.Application)
            .BrokerRole.ShouldBe("stream");
    }
}
