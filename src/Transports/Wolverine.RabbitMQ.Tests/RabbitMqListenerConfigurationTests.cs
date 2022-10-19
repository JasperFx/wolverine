using NSubstitute;
using Shouldly;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Runtime;
using Xunit;

namespace Wolverine.RabbitMQ.Tests
{
    public class RabbitMqListenerConfigurationTests
    {
        [Fact]
        public void override_prefetch_count()
        {
            var endpoint = new RabbitMqQueue("foo",new RabbitMqTransport());
            var expression = new RabbitMqListenerConfiguration(endpoint);

            expression.PreFetchCount(99).ShouldBeSameAs(expression);
            
            var wolverineRuntime = Substitute.For<IWolverineRuntime>();
            wolverineRuntime.Options.Returns(new WolverineOptions());
            
            endpoint.Compile(wolverineRuntime);

            endpoint.PreFetchCount.ShouldBe((ushort)99);
        }

        [Fact]
        public void override_prefetch_size()
        {
            var endpoint = new RabbitMqQueue("foo", new RabbitMqTransport());
            var expression = new RabbitMqListenerConfiguration(endpoint);

            expression.PreFetchSize(1111).ShouldBeSameAs(expression);
            var wolverineRuntime = Substitute.For<IWolverineRuntime>();
            wolverineRuntime.Options.Returns(new WolverineOptions());
            
            endpoint.Compile(wolverineRuntime);

            endpoint.PreFetchSize.ShouldBe((uint)1111);
        }
    }
}
