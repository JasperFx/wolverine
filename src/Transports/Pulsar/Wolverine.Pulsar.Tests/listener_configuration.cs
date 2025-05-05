using JasperFx.Core.Reflection;
using Shouldly;
using Wolverine.Configuration;
using Xunit;

namespace Wolverine.Pulsar.Tests;

public class listener_configuration
{
    [Fact]
    public void disable_requeue()
    {
        var transport = new PulsarTransport();
        var endpoint = new PulsarEndpoint(new Uri("pulsar://persistent/default/test/test"), transport);
        var listenerConfiguration = new PulsarListenerConfiguration(endpoint);

        listenerConfiguration.DisableRequeue();

        endpoint.EnableRequeue.ShouldBeTrue();

        listenerConfiguration.As<IDelayedEndpointConfiguration>().Apply();

        endpoint.EnableRequeue.ShouldBeFalse();
    }
}
