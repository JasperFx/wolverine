using Wolverine.Nats.Configuration;
using Xunit;
using Shouldly;

namespace Wolverine.Nats.Tests;

public class BasicTests
{
    [Fact]
    public void Transport_configuration_has_sensible_defaults()
    {
        var config = new NatsTransportConfiguration();

        config.ConnectionString.ShouldBe("nats://localhost:4222");
        config.ConnectTimeout.ShouldBe(TimeSpan.FromSeconds(10));
        config.RequestTimeout.ShouldBe(TimeSpan.FromSeconds(30));
        config.EnableJetStream.ShouldBeTrue();
    }
}
