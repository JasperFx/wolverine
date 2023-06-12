using System;
using System.Linq;
using JasperFx.Core;
using Shouldly;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Util;
using Xunit;

namespace Wolverine.RabbitMQ.Tests.ConventionalRouting;

public class when_discovering_a_listening_endpoint_with_overridden_queue_naming : ConventionalRoutingContext
{
    private readonly RabbitMqEndpoint theEndpoint;
    private readonly Uri theExpectedUri = "rabbitmq://queue/routed2".ToUri();

    public when_discovering_a_listening_endpoint_with_overridden_queue_naming()
    {
        ConfigureConventions(c => c.QueueNameForListener(t => t.ToMessageTypeName() + "2"));

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
    public void should_be_an_active_listener()
    {
        theRuntime.Endpoints.ActiveListeners().Any(x => x.Uri == theExpectedUri)
            .ShouldBeTrue();
    }

    [Fact]
    public void the_queue_was_declared()
    {
        theTransport.Queues.Contains("routed2").ShouldBeTrue();
        theTransport.Queues["routed2"].HasDeclared.ShouldBeTrue();
    }
}