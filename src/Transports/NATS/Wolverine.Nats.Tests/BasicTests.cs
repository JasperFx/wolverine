using Wolverine.Nats.Configuration;
using Xunit;
using FluentAssertions;

namespace Wolverine.Nats.Tests;

public class BasicTests
{
    [Fact]
    public void Transport_configuration_has_sensible_defaults()
    {
        var config = new NatsTransportConfiguration();

        config.ConnectionString.Should().Be("nats://localhost:4222");
        config.ConnectTimeout.Should().Be(TimeSpan.FromSeconds(10));
        config.RequestTimeout.Should().Be(TimeSpan.FromSeconds(30));
        config.EnableJetStream.Should().BeTrue();
    }
}
