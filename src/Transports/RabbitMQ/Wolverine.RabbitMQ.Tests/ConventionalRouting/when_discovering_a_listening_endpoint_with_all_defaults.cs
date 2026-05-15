using JasperFx.Core;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.RabbitMQ.Internal;
using Xunit;

namespace Wolverine.RabbitMQ.Tests.ConventionalRouting;

public class when_discovering_a_listening_endpoint_with_all_defaults : ConventionalRoutingContext, IAsyncLifetime
{
    private RabbitMqEndpoint theEndpoint = null!;
    private readonly Uri theExpectedUri = "rabbitmq://queue/routed".ToUri();

    public async Task InitializeAsync()
    {
        await ConfigureConventions(x=> x.IncludeTypes(ConventionalRoutingTestDefaults.RoutingMessageOnly));
        theEndpoint = (await theRuntime()).Endpoints.EndpointFor(theExpectedUri).ShouldBeOfType<RabbitMqQueue>();
    }

    Task IAsyncLifetime.DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void endpoint_should_be_a_listener()
    {
        theEndpoint.IsListener.ShouldBeTrue();
    }

    [Fact]
    public void endpoint_should_not_be_null()
    {
        theEndpoint.ShouldNotBeNull();
    }

    [Fact]
    public void mode_is_inline_by_default()
    {
        theEndpoint.Mode.ShouldBe(EndpointMode.Inline);
    }

    [Fact]
    public async Task should_be_an_active_listener()
    {
        (await theRuntime()).Endpoints.ActiveListeners().Any(x => x.Uri == theExpectedUri)
            .ShouldBeTrue();
    }

    [Fact]
    public async Task the_queue_was_declared()
    {
        var transport = await theTransport();
        transport.Queues.Contains("routed").ShouldBeTrue();
        transport.Queues["routed"].HasDeclared.ShouldBeTrue();
    }
}
