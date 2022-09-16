using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using RabbitMQ.Client;
using Shouldly;
using Wolverine.RabbitMQ.Internal;
using Xunit;

namespace Wolverine.RabbitMQ.Tests
{
    public class RabbitMqTransportTests
    {
        private readonly RabbitMqTransport theTransport = new RabbitMqTransport();
        private readonly IModel theChannel = Substitute.For<IModel>();


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
        public void initialize_with_no_auto_provision_or_auto_purge()
        {
            theTransport.AutoProvision = false;
            theTransport.AutoPurgeAllQueues = false;

            var endpoint = new RabbitMqEndpoint(theTransport)
            {
                QueueName = "foo",
                ExchangeName = "bar",

            };

            theTransport.Queues["foo"].PurgeOnStartup = false;

            theTransport.InitializeEndpoint(endpoint, theChannel, NullLogger.Instance);

            theChannel.DidNotReceiveWithAnyArgs().QueueDeclare("foo", true, true, true, null);
            theChannel.DidNotReceiveWithAnyArgs().ExchangeDeclare("bar", "fanout", true, false, null);
            theChannel.DidNotReceiveWithAnyArgs().QueuePurge("foo");
        }


        [Fact]
        public void initialize_with_no_auto_provision_but_auto_purge_on_endpoint_only()
        {
            theTransport.AutoProvision = false;
            theTransport.AutoPurgeAllQueues = false;

            var endpoint = new RabbitMqEndpoint(theTransport)
            {
                QueueName = "foo",
                ExchangeName = "bar",

            };

            theTransport.Queues["foo"].PurgeOnStartup = true;

            theTransport.InitializeEndpoint(endpoint, theChannel, NullLogger.Instance);

            theChannel.DidNotReceiveWithAnyArgs().QueueDeclare("foo", true, true, true, null);
            theChannel.DidNotReceiveWithAnyArgs().ExchangeDeclare("bar", "fanout", true, false, null);
            theChannel.Received().QueuePurge("foo");
        }

        [Fact]
        public void initialize_with_no_auto_provision_but_global_auto_purge()
        {
            theTransport.AutoProvision = false;
            theTransport.AutoPurgeAllQueues = true;

            var endpoint = new RabbitMqEndpoint(theTransport)
            {
                QueueName = "foo",
                ExchangeName = "bar",

            };

            theTransport.Queues["foo"].PurgeOnStartup = false;

            theTransport.InitializeEndpoint(endpoint, theChannel, NullLogger.Instance);

            theChannel.DidNotReceiveWithAnyArgs().QueueDeclare("foo", true, true, true, null);
            theChannel.DidNotReceiveWithAnyArgs().ExchangeDeclare("bar", "fanout", true, false, null);
            theChannel.Received().QueuePurge("foo");
        }

        [Fact]
        public void initialize_with_auto_provision_and_global_auto_purge()
        {
            theTransport.AutoProvision = true;
            theTransport.AutoPurgeAllQueues = true;

            var endpoint = new RabbitMqEndpoint(theTransport)
            {
                QueueName = "foo",
                ExchangeName = "bar",

            };

            theTransport.Queues["foo"].PurgeOnStartup = false;

            theTransport.InitializeEndpoint(endpoint, theChannel, NullLogger.Instance);

            theChannel.Received().QueueDeclare("foo", true, false, false, theTransport.Queues["foo"].Arguments);
            theChannel.Received().ExchangeDeclare("bar", "fanout", true, false, theTransport.Exchanges["bar"].Arguments);
            theChannel.Received().QueuePurge("foo");
        }

        [Fact]
        public void initialize_with_auto_provision_and_local_auto_purge()
        {
            theTransport.AutoProvision = true;
            theTransport.AutoPurgeAllQueues = false;

            var endpoint = new RabbitMqEndpoint(theTransport)
            {
                QueueName = "foo",
                ExchangeName = "bar",

            };

            theTransport.Queues["foo"].PurgeOnStartup = true;

            theTransport.InitializeEndpoint(endpoint, theChannel, NullLogger.Instance);

            theChannel.Received().QueueDeclare("foo", true, false, false, theTransport.Queues["foo"].Arguments);
            theChannel.Received().ExchangeDeclare("bar", "fanout", true, false, theTransport.Exchanges["bar"].Arguments);
            theChannel.Received().QueuePurge("foo");
        }
    }
}
