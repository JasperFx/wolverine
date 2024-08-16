using JasperFx.Core.Reflection;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Runtime;
using Wolverine.Runtime.Routing;
using Wolverine.Util;
using Xunit;

namespace Wolverine.RabbitMQ.Tests.ConventionalRouting;

public class when_discovering_a_sender_with_all_defaults : ConventionalRoutingContext
{
    private readonly MessageRoute theRoute;
    public when_discovering_a_sender_with_all_defaults()
    {
        DisableListenerDiscovery = true;
        ConfigureConventions(x=> x.IncludeTypes(ConventionalRoutingTestDefaults.RoutingMessageOnly));
        theRoute = PublishingRoutesFor<ConventionallyRoutedMessage>().Single() as MessageRoute;
    }

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
        await new MessageBus(theRuntime).SendAsync(new ConventionallyRoutedMessage());

        var endpoint = theRoute.Sender.Endpoint.ShouldBeOfType<RabbitMqExchange>();
        theTransport.Exchanges.Contains(endpoint.ExchangeName).ShouldBeTrue();
        var theExchange = theTransport.Exchanges[endpoint.ExchangeName];
        theExchange.HasDeclared.ShouldBeTrue();
    }

   /* [Fact]
    public async Task has_bound_the_exchange_to_a_queue_of_the_same_name()
    {
        // The rabbit object construction is lazy, so force it to happen
        await new MessageBus(theRuntime).SendAsync(new PublishedMessage());

        var endpoint = theRoute.Sender.Endpoint.ShouldBeOfType<RabbitMqExchange>();
        var theQueue = theTransport.Queues[endpoint.ExchangeName];
        var binding = theQueue.Bindings().Single().ShouldNotBeNull();
        var theExchange = theTransport.Exchanges[endpoint.ExchangeName];
        binding.Queue.As<RabbitMqQueue>().EndpointName.ShouldBe(theExchange.Name);
        binding.Queue.As<RabbitMqQueue>().HasDeclared.ShouldBeTrue();
        binding.HasDeclared.ShouldBeTrue();
    }*/
}