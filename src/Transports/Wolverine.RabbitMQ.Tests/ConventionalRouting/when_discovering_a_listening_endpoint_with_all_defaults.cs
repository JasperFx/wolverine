using System;
using System.Linq;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Util;
using Xunit;

namespace Wolverine.RabbitMQ.Tests.ConventionalRouting;

public class when_discovering_a_listening_endpoint_with_all_defaults : ConventionalRoutingContext
{
    private readonly RabbitMqEndpoint theEndpoint;
    private readonly Uri theExpectedUri = "rabbitmq://queue/routed".ToUri();

    public when_discovering_a_listening_endpoint_with_all_defaults()
    {
        theEndpoint = theRuntime.Endpoints.EndpointFor(theExpectedUri).ShouldBeOfType<RabbitMqQueue>();
    }

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
    public void should_be_an_active_listener()
    {
        theRuntime.Endpoints.ActiveListeners().Any(x => x.Uri == theExpectedUri)
            .ShouldBeTrue();
    }

    [Fact]
    public void the_queue_was_declared()
    {
        theTransport.Queues.Has("routed").ShouldBeTrue();
        theTransport.Queues["routed"].HasDeclared.ShouldBeTrue();
    }
}