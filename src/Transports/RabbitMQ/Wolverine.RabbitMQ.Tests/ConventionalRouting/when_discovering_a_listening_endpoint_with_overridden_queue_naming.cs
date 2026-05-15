using JasperFx.Core;
using Shouldly;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Util;
using Xunit;

namespace Wolverine.RabbitMQ.Tests.ConventionalRouting;

public class when_discovering_a_listening_endpoint_with_overridden_queue_naming : ConventionalRoutingContext, IAsyncLifetime
{
    private RabbitMqEndpoint theEndpoint = null!;
    private readonly Uri theExpectedUri = "rabbitmq://queue/routed2".ToUri();

    public async Task InitializeAsync()
    {
        await ConfigureConventions(c =>
        {
            c.IncludeTypes(ConventionalRoutingTestDefaults.RoutingMessageOnly);
            c.QueueNameForListener(t => t.ToMessageTypeName() + "2");
        });

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
    public async Task should_be_an_active_listener()
    {
        (await theRuntime()).Endpoints.ActiveListeners().Any(x => x.Uri == theExpectedUri)
            .ShouldBeTrue();
    }

    [Fact]
    public async Task the_queue_was_declared()
    {
        var transport = await theTransport();
        transport.Queues.Contains("routed2").ShouldBeTrue();
        transport.Queues["routed2"].HasDeclared.ShouldBeTrue();
    }
}
