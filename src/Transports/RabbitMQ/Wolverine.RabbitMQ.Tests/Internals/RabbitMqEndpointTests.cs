using JasperFx.Core;
using NSubstitute;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Runtime;
using Xunit;

namespace Wolverine.RabbitMQ.Tests.Internals;

public class RabbitMqEndpointTests
{
    [Fact]
    public void default_routing_mode_is_static()
    {
        var endpoint = new RabbitMqQueue("foo", new RabbitMqTransport());
        endpoint.RoutingType.ShouldBe(RoutingMode.Static);
    }

    [Fact]
    public void override_the_prefetch_count()
    {
        var endpoint = new RabbitMqQueue("foo", new RabbitMqTransport());
        endpoint.PreFetchCount = 67;

        endpoint.PreFetchCount.ShouldBe((ushort)67);
    }

    [Fact]
    public void if_buffered_use_twice_count_of_maximum_parallelism_as_the_prefetch_COUNT()
    {
        var endpoint = new RabbitMqQueue("foo", new RabbitMqTransport());
        endpoint.Mode = EndpointMode.BufferedInMemory;
        new RabbitMqListenerConfiguration(endpoint, new RabbitMqTransport()).MaximumParallelMessages(10);

        var wolverineRuntime = Substitute.For<IWolverineRuntime>();
        wolverineRuntime.Options.Returns(new WolverineOptions());

        endpoint.Compile(wolverineRuntime);
        endpoint.PreFetchCount.ShouldBe((ushort)20);
    }

    [Fact]
    public void if_durable_use_twice_count_of_maximum_parallelism_as_the_prefetch_COUNT()
    {
        var endpoint = new RabbitMqQueue("foo", new RabbitMqTransport());
        endpoint.Mode = EndpointMode.Durable;

        new RabbitMqListenerConfiguration(endpoint, new RabbitMqTransport()).MaximumParallelMessages(10);
        var wolverineRuntime = Substitute.For<IWolverineRuntime>();
        wolverineRuntime.Options.Returns(new WolverineOptions());

        endpoint.Compile(wolverineRuntime);

        endpoint.PreFetchCount.ShouldBe((ushort)20);
    }

    [Fact]
    public void if_inline_use_100_as_the_default_prefetch_count()
    {
        var endpoint = new RabbitMqQueue("foo", new RabbitMqTransport());
        endpoint.Mode = EndpointMode.Inline;
        new RabbitMqListenerConfiguration(endpoint, new RabbitMqTransport()).MaximumParallelMessages(10);

        endpoint.PreFetchCount.ShouldBe((ushort)100);
    }

    [Fact]
    public void map_to_rabbit_mq_uri_with_queue()
    {
        var transport = new RabbitMqTransport();
        transport.ConfigureFactory(f => f.HostName = "rabbitserver");

        var endpoint = new RabbitMqQueue("foo", transport);

        // No virtual host

        endpoint.MassTransitUri().ShouldBe("rabbitmq://rabbitserver/foo".ToUri());

        // With virtual host

        transport.ConnectionFactory.VirtualHost = "v1";

        endpoint.MassTransitUri().ShouldBe("rabbitmq://rabbitserver/v1/foo".ToUri());
    }

    [Fact]
    public void map_to_rabbit_mq_uri_with_exchange()
    {
        var transport = new RabbitMqTransport();
        transport.ConfigureFactory(f => f.HostName = "rabbitserver");

        var endpoint = new RabbitMqQueue("bar", transport);

        // No virtual host

        endpoint.MassTransitUri().ShouldBe("rabbitmq://rabbitserver/bar".ToUri());

        // With virtual host

        transport.ConnectionFactory.VirtualHost = "v1";

        endpoint.MassTransitUri().ShouldBe("rabbitmq://rabbitserver/v1/bar".ToUri());
    }

    [Theory]
    [InlineData(EndpointMode.BufferedInMemory, true)]
    [InlineData(EndpointMode.Durable, true)]
    [InlineData(EndpointMode.Inline, false)]
    public void should_enforce_back_pressure(EndpointMode mode, bool shouldEnforce)
    {
        var endpoint = new RabbitMqQueue("foo", new RabbitMqTransport());
        endpoint.Mode = mode;
        endpoint.ShouldEnforceBackPressure().ShouldBe(shouldEnforce);
    }
}