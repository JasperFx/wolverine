using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using RabbitMQ.Client;
using Shouldly;
using Wolverine.RabbitMQ.Internal;
using Xunit;

namespace Wolverine.RabbitMQ.Tests;

public class RabbitMqExchangeBindingTests
{
    [Fact]
    public async Task declare_calls_exchange_bind()
    {
        var channel = Substitute.For<IChannel>();
        var binding = new RabbitMqExchangeBinding("source", "destination", "routing.key");

        await binding.DeclareAsync(channel, NullLogger.Instance);

        await channel.Received().ExchangeBindAsync("destination", "source", "routing.key", binding.Arguments);
        binding.HasDeclared.ShouldBeTrue();
    }

    [Fact]
    public async Task teardown_calls_exchange_unbind()
    {
        var channel = Substitute.For<IChannel>();
        var binding = new RabbitMqExchangeBinding("source", "destination", "routing.key");

        await binding.TeardownAsync(channel);

        await channel.Received().ExchangeUnbindAsync("destination", "source", "routing.key", binding.Arguments);
    }
    
    public class when_adding_exchange_to_exchange_bindings
    {
        private readonly RabbitMqTransport theTransport = new();

        public when_adding_exchange_to_exchange_bindings()
        {
            new RabbitMqTransportExpression(theTransport, new WolverineOptions())
                .BindExchange("source-exchange").ToExchange("destination-exchange", "routing.key");
        }

        [Fact]
        public void should_add_the_exchange_binding()
        {
            var destExchange = theTransport.Exchanges["destination-exchange"];
            var binding = destExchange.ExchangeBindings().Single();
            binding.SourceExchangeName.ShouldBe("source-exchange");
            binding.DestinationExchangeName.ShouldBe("destination-exchange");
            binding.BindingKey.ShouldBe("routing.key");
        }

        [Fact]
        public void should_declare_the_source_exchange()
        {
            theTransport.Exchanges.Contains("source-exchange").ShouldBeTrue();
        }

        [Fact]
        public void should_declare_the_destination_exchange()
        {
            theTransport.Exchanges.Contains("destination-exchange").ShouldBeTrue();
        }

        [Fact]
        public void add_binding_without_routing_key()
        {
            new RabbitMqTransportExpression(theTransport, new WolverineOptions())
                .BindExchange("source2").ToExchange("destination2");

            var destExchange = theTransport.Exchanges["destination2"];
            destExchange.ExchangeBindings()
                .ShouldContain(x => x.BindingKey == "source2_destination2");
        }
    }


    public class exchange_binding_via_declare_exchange
    {
        private readonly RabbitMqTransport theTransport = new();

        [Fact]
        public void bind_exchange_via_declare_exchange_configuration()
        {
            new RabbitMqTransportExpression(theTransport, new WolverineOptions())
                .DeclareExchange("destination", exchange =>
                {
                    exchange.ExchangeType = ExchangeType.Topic;
                    exchange.BindExchange("source", "routing.*");
                });

            var destExchange = theTransport.Exchanges["destination"];
            destExchange.ExchangeType.ShouldBe(ExchangeType.Topic);

            var binding = destExchange.ExchangeBindings().Single();
            binding.SourceExchangeName.ShouldBe("source");
            binding.BindingKey.ShouldBe("routing.*");
        }

        [Fact]
        public void bind_exchange_deduplicates()
        {
            var exchange = theTransport.Exchanges["dest"];
            exchange.BindExchange("source", "key");
            exchange.BindExchange("source", "key");

            exchange.ExchangeBindings().Count().ShouldBe(1);
        }

        [Fact]
        public void bind_exchange_different_keys_are_separate()
        {
            var exchange = theTransport.Exchanges["dest"];
            exchange.BindExchange("source", "key1");
            exchange.BindExchange("source", "key2");

            exchange.ExchangeBindings().Count().ShouldBe(2);
        }

        [Fact]
        public void bind_exchange_with_arguments()
        {
            var exchange = theTransport.Exchanges["dest"];
            var args = new Dictionary<string, object> { { "x-match", "any" } };
            var binding = exchange.BindExchange("source", "key", args);

            binding.Arguments.ShouldContainKeyAndValue("x-match", "any");
        }

        [Fact]
        public void bind_exchange_ensures_source_exchange_exists()
        {
            var exchange = theTransport.Exchanges["dest"];
            exchange.BindExchange("auto-created-source", "key");

            theTransport.Exchanges.Contains("auto-created-source").ShouldBeTrue();
        }

        [Fact]
        public void has_exchange_bindings_is_false_by_default()
        {
            var exchange = theTransport.Exchanges["empty"];
            exchange.HasExchangeBindings.ShouldBeFalse();
        }

        [Fact]
        public void has_exchange_bindings_is_true_after_adding()
        {
            var exchange = theTransport.Exchanges["dest"];
            exchange.BindExchange("source", "key");
            exchange.HasExchangeBindings.ShouldBeTrue();
        }

        [Fact]
        public void bind_exchange_throws_on_null_source()
        {
            var exchange = theTransport.Exchanges["dest"];
            Should.Throw<ArgumentNullException>(() => exchange.BindExchange(null!));
        }
    }

    public class exchange_declare_with_exchange_bindings
    {
        [Fact]
        public async Task declare_async_also_declares_exchange_bindings()
        {
            var channel = Substitute.For<IChannel>();
            var transport = new RabbitMqTransport();
            var exchange = transport.Exchanges["dest"];
            exchange.ExchangeType = ExchangeType.Topic;
            exchange.BindExchange("source", "routing.key");

            await exchange.DeclareAsync(channel, NullLogger.Instance);

            await channel.Received().ExchangeDeclareAsync("dest", "topic", true, false, exchange.Arguments);
            await channel.Received().ExchangeBindAsync("dest", "source", "routing.key",
                Arg.Any<IDictionary<string, object?>>());
        }
    }
}
