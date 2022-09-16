using System.Linq;
using Shouldly;
using Wolverine.RabbitMQ.Internal;
using Xunit;

namespace Wolverine.RabbitMQ.Tests
{
    public class when_adding_bindings
    {
        private readonly RabbitMqTransport theTransport = new();

        public when_adding_bindings()
        {
            new RabbitMqTransportExpression(theTransport, new WolverineOptions())
                .BindExchange("exchange3").ToQueue("queue3", "key3");
        }

        [Fact]
        public void should_add_the_binding()
        {
            var binding = theTransport.Bindings().Single();
            binding.BindingKey.ShouldBe("key3");
            binding.ExchangeName.ShouldBe("exchange3");
            binding.Queue.Name.ShouldBe("queue3");
        }

        [Fact]
        public void add_binding_without_routing_key()
        {
            new RabbitMqTransportExpression(theTransport, new WolverineOptions())
                .BindExchange("exchange3").ToQueue("queue1");

            theTransport.Bindings().ShouldContain(x => x.BindingKey == "exchange3_queue1");
        }

        [Fact]
        public void should_declare_the_exchange()
        {
            theTransport.Exchanges.Has("exchange3").ShouldBeTrue();
        }

        [Fact]
        public void should_declare_the_queue()
        {
            theTransport.Queues.Has("queue3").ShouldBeTrue();
        }
    }
}
