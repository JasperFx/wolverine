using System;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using RabbitMQ.Client;
using Shouldly;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Runtime;
using Wolverine.Util;
using Xunit;

namespace Wolverine.RabbitMQ.Tests
{
    public class RabbitMqTransportTests
    {
        private readonly RabbitMqTransport theTransport = new RabbitMqTransport
        {
            
        };


        [Fact]
        public void automatic_recovery_is_try_by_default()
        {
            theTransport.ConnectionFactory.AutomaticRecoveryEnabled.ShouldBeTrue();
        }

        [Fact]
        public void auto_provision_is_false_by_default()
        {
            theTransport.AutoProvision.ShouldBeFalse();
        }

        [Fact]
        public void find_by_uri_for_exchange()
        {
            var exchange = theTransport.GetOrCreateEndpoint("rabbitmq://exchange/foo".ToUri())
                .ShouldBeOfType<RabbitMqExchange>();
            
            exchange.ExchangeName.ShouldBe("foo");
        }

        [Fact]
        public void find_by_uri_for_queue()
        {
            var queue = theTransport.GetOrCreateEndpoint("rabbitmq://queue/foo".ToUri())
                .ShouldBeOfType<RabbitMqQueue>();
            
            queue.QueueName.ShouldBe("foo");
        }

    }


}


