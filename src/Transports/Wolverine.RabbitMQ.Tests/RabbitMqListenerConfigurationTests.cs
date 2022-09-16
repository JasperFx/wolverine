using Shouldly;
using Wolverine.RabbitMQ.Internal;
using Xunit;

namespace Wolverine.RabbitMQ.Tests
{
    public class RabbitMqListenerConfigurationTests
    {
        [Fact]
        public void override_prefetch_count()
        {
            var endpoint = new RabbitMqEndpoint(new RabbitMqTransport());
            var expression = new RabbitMqListenerConfiguration(endpoint);

            expression.PreFetchCount(99).ShouldBeSameAs(expression);

            endpoint.PreFetchCount.ShouldBe((ushort)99);
        }

        [Fact]
        public void override_prefetch_size()
        {
            var endpoint = new RabbitMqEndpoint(new RabbitMqTransport());
            var expression = new RabbitMqListenerConfiguration(endpoint);

            expression.PreFetchSize(1111).ShouldBeSameAs(expression);

            endpoint.PreFetchSize.ShouldBe((uint)1111);
        }
    }
}
