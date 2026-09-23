using JasperFx.Core.Reflection;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Runtime;
using Wolverine.Runtime.Routing;
using Wolverine.Util;
using Xunit;

namespace Wolverine.RabbitMQ.Tests.ConventionalRouting;

public class when_discovering_a_sender_with_all_defaults : ConventionalRoutingContext, IAsyncLifetime
{
    private MessageRoute theRoute = null!;

    public async ValueTask InitializeAsync()
    {
        DisableListenerDiscovery = true;
        await ConfigureConventions(x=> x.IncludeTypes(ConventionalRoutingTestDefaults.RoutingMessageOnly));
        theRoute = ((await PublishingRoutesFor<ConventionallyRoutedMessage>()).Single() as MessageRoute)!;
    }

    // GH-3965: shadows ConventionalRoutingContext.DisposeAsync -- must stop the host itself.
    ValueTask IAsyncDisposable.DisposeAsync() => DisposeHostAsync();

    [Fact]
    public void should_have_exactly_one_route()
    {
        theRoute.ShouldNotBeNull();
    }

    [Fact]
    public void routed_to_rabbit_mq_exchange()
    {
        var endpoint = theRoute.Sender.Endpoint.ShouldBeOfType<RabbitMqExchange>();
        endpoint.ExchangeName.ShouldBe(typeof(ConventionallyRoutedMessage).ToMessageTypeName());
    }

    [Fact]
    public void endpoint_mode_is_inline_by_default()
    {
        var endpoint = theRoute.Sender.Endpoint.ShouldBeOfType<RabbitMqExchange>();
        endpoint.Mode.ShouldBe(EndpointMode.Inline);
    }

    [Fact]
    public async Task has_declared_exchange()
    {
        // The rabbit object construction is lazy, so force it to happen
        await new MessageBus(await theRuntime()).SendAsync(new ConventionallyRoutedMessage());

        var endpoint = theRoute.Sender.Endpoint.ShouldBeOfType<RabbitMqExchange>();
        var transport = await theTransport();
        transport.Exchanges.Contains(endpoint.ExchangeName).ShouldBeTrue();
        var theExchange = transport.Exchanges[endpoint.ExchangeName];
        theExchange.HasDeclared.ShouldBeTrue();
    }

    // GH-4559: a has_bound_the_exchange_to_a_queue_of_the_same_name test sat here commented out -- the one
    // test in this file that would have covered bindings, and dead for long enough that it still referred
    // to theRuntime/theTransport as properties. It cannot hold in THIS fixture anyway: DisableListenerDiscovery
    // is set above, so the convention only ever creates the exchange and there is no queue to bind to. The
    // coverage now lives in when_discovering_a_listening_endpoint_with_all_defaults, where listener
    // discovery is on and the broker can be asked about the binding.
}
