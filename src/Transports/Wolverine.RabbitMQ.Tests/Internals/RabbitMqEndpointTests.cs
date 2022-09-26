using System;
using NSubstitute;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Runtime;
using Wolverine.Util;
using Xunit;

namespace Wolverine.RabbitMQ.Tests.Internals
{
    public class RabbitMqEndpointTests
    {

        [Fact]
        public void parse_non_durable_uri()
        {
            var endpoint = new RabbitMqEndpoint(new RabbitMqTransport());
            endpoint.Parse(new Uri("rabbitmq://exchange/exchange1/routing/key1"));

            endpoint.Mode.ShouldBe(EndpointMode.Inline);
            endpoint.ExchangeName.ShouldBe("exchange1");
            endpoint.RoutingKey.ShouldBe("key1");
        }

        [Fact]
        public void default_routing_mode_is_static()
        {
            var endpoint = new RabbitMqEndpoint(new RabbitMqTransport());
            endpoint.RoutingType.ShouldBe(RoutingMode.Static);
        }

        [Fact]
        public void default_prefetch_size_is_0()
        {
            var endpoint = new RabbitMqEndpoint(new RabbitMqTransport());
            endpoint.PreFetchSize.ShouldBe((uint)0);
        }

        [Fact]
        public void override_the_prefetch_count()
        {
            var endpoint = new RabbitMqEndpoint(new RabbitMqTransport());
            endpoint.PreFetchCount = 67;

            endpoint.PreFetchCount.ShouldBe((ushort)67);
        }

        [Fact]
        public void if_buffered_use_twice_count_of_maximum_parallelism_as_the_prefetch_COUNT()
        {
            var endpoint = new RabbitMqEndpoint(new RabbitMqTransport());
            endpoint.Mode = EndpointMode.BufferedInMemory;
            new RabbitMqListenerConfiguration(endpoint).MaximumParallelMessages(10);

            var wolverineRuntime = Substitute.For<IWolverineRuntime>();
            wolverineRuntime.Options.Returns(new WolverineOptions());
            
            endpoint.Compile(wolverineRuntime);
            endpoint.PreFetchCount.ShouldBe((ushort)20);
        }

        [Fact]
        public void if_durable_use_twice_count_of_maximum_parallelism_as_the_prefetch_COUNT()
        {
            var endpoint = new RabbitMqEndpoint(new RabbitMqTransport());
            endpoint.Mode = EndpointMode.Durable;
            
            new RabbitMqListenerConfiguration(endpoint).MaximumParallelMessages(10);
            var wolverineRuntime = Substitute.For<IWolverineRuntime>();
            wolverineRuntime.Options.Returns(new WolverineOptions());
            
            endpoint.Compile(wolverineRuntime);

            endpoint.PreFetchCount.ShouldBe((ushort)20);
        }

        [Fact]
        public void if_inline_use_100_as_the_default_prefetch_count()
        {
            var endpoint = new RabbitMqEndpoint(new RabbitMqTransport());
            endpoint.Mode = EndpointMode.Inline;
            new RabbitMqListenerConfiguration(endpoint).MaximumParallelMessages(10);

            endpoint.PreFetchCount.ShouldBe((ushort)100);
        }







        [Fact]
        public void parse_durable_uri()
        {
            var endpoint = new RabbitMqEndpoint(new RabbitMqTransport());
            endpoint.Parse(new Uri("rabbitmq://exchange/exchange1/routing/key1/durable"));

            endpoint.Mode.ShouldBe(EndpointMode.Durable);
            endpoint.ExchangeName.ShouldBe("exchange1");
            endpoint.RoutingKey.ShouldBe("key1");
        }

        [Fact]
        public void parse_durable_uri_with_only_queue()
        {
            var endpoint = new RabbitMqEndpoint(new RabbitMqTransport());
            endpoint.Parse(new Uri("rabbitmq://queue/q1/durable"));

            endpoint.Mode.ShouldBe(EndpointMode.Durable);
            endpoint.QueueName.ShouldBe("q1");
        }

        [Fact]
        public void build_uri_for_exchange_and_routing()
        {
            new RabbitMqEndpoint(new RabbitMqTransport())
                {
                    ExchangeName = "ex1",
                    RoutingKey = "key1"
                }
                .Uri.ShouldBe(new Uri("rabbitmq://exchange/ex1/routing/key1"));
        }

        [Fact]
        public void build_uri_for_queue_only()
        {
            new RabbitMqEndpoint(new RabbitMqTransport())
                {
                    QueueName = "foo"
                }
                .Uri.ShouldBe(new Uri("rabbitmq://queue/foo"));
        }


        [Fact]
        public void build_uri_for_exchange_only()
        {
            new RabbitMqEndpoint(new RabbitMqTransport())
            {
                ExchangeName = "ex2"

            }.Uri.ShouldBe("rabbitmq://exchange/ex2".ToUri());
        }

        [Fact]
        public void build_uri_for_exchange_and_topics()
        {
            new RabbitMqEndpoint(new RabbitMqTransport())
            {
                ExchangeName = "ex2"

            }.Uri.ShouldBe("rabbitmq://exchange/ex2".ToUri());
        }


        [Fact]
        public void map_to_rabbit_mq_uri_with_queue()
        {
            var transport = new RabbitMqTransport();
            transport.ConnectionFactory.HostName = "rabbitserver";

            var endpoint = new RabbitMqEndpoint(transport);
            endpoint.QueueName = "foo";

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
            transport.ConnectionFactory.HostName = "rabbitserver";

            var endpoint = new RabbitMqEndpoint(transport);
            endpoint.ExchangeName = "bar";

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
            var endpoint = new RabbitMqEndpoint(new RabbitMqTransport());
            endpoint.Mode = mode;
            endpoint.ShouldEnforceBackPressure().ShouldBe(shouldEnforce);
        }


    }
}
