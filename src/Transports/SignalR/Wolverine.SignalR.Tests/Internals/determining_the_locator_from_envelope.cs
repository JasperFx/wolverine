using Shouldly;
using Wolverine.SignalR.Internals;

namespace Wolverine.SignalR.Tests.Internals;

public class determining_the_locator_from_envelope
{
    [Fact]
    public void no_information_so_it_is_all()
    {
        var envelope = new Envelope();

        WebSocketRouting.DetermineLocator(envelope)
            .ShouldBeOfType<WebSocketRouting.All>();
    }

    [Fact]
    public void ignore_garbage_on_the_envelope_routing()
    {
        var envelope = new Envelope
        {
            RoutingInformation = "junk"
        };

        WebSocketRouting.DetermineLocator(envelope)
            .ShouldBeOfType<WebSocketRouting.All>();
    }

    [Fact]
    public void use_routing_information_if_it_is_a_locator()
    {
        var locator = new WebSocketRouting.Group("foo");
        var envelope = new Envelope
        {
            RoutingInformation = locator
        };
        
        WebSocketRouting.DetermineLocator(envelope)
            .ShouldBe(locator);
    }

    [Fact]
    public void try_parse_saga_id()
    {
        var locator = new WebSocketRouting.Group("foo");
        var envelope = new Envelope
        {
            SagaId = locator.ToString()
        };
        
        WebSocketRouting.DetermineLocator(envelope)
            .ShouldBe(locator);
    }
}