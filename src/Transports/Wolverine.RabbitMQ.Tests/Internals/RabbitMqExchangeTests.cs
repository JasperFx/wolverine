using System;
using Baseline.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using RabbitMQ.Client;
using Shouldly;
using Wolverine.RabbitMQ.Internal;
using Xunit;
using ExchangeType = Wolverine.RabbitMQ.ExchangeType;

namespace Wolverine.RabbitMQ.Tests.Internals
{
    public class configuration_model_specs
    {
        private readonly RabbitMqTransport theTransport = new RabbitMqTransport
        {
            
        };
        private readonly IModel theChannel = Substitute.For<IModel>();
        
        [Fact]
        public void defaults()
        {
            var exchange = new RabbitMqExchange("foo", new RabbitMqTransport());
            exchange.Name.ShouldBe("foo");
            exchange.ExchangeType.ShouldBe(ExchangeType.Fanout);
            exchange.AutoDelete.ShouldBeFalse();
            exchange.IsDurable.ShouldBeTrue();
        }

        [Fact]
        public void uri_construction()
        {
            var exchange = new RabbitMqExchange("foo", new RabbitMqTransport());
            exchange.Uri.ShouldBe(new Uri("rabbitmq://exchange/foo"));
        }

        [Fact]
        public void exchange_declare()
        {
            var channel = Substitute.For<IModel>();
            var exchange = new RabbitMqExchange("foo", new RabbitMqTransport())
            {
                ExchangeType = ExchangeType.Fanout,
                AutoDelete = true,
                IsDurable = false
            };

            exchange.Declare(channel, NullLogger.Instance);

            channel.Received().ExchangeDeclare("foo", "fanout", false, true, exchange.Arguments);

            exchange.HasDeclared.ShouldBeTrue();
        }

        [Fact]
        public void already_latched()
        {
            var channel = Substitute.For<IModel>();
            var exchange = new RabbitMqExchange("foo", new RabbitMqTransport())
            {
                ExchangeType = ExchangeType.Fanout,
                AutoDelete = true,
                IsDurable = false
            };

            // cheating here.
            var prop = ReflectionHelper.GetProperty<RabbitMqExchange>(x => x.HasDeclared);
            prop.SetValue(exchange, true);

            exchange.Declare(channel, NullLogger.Instance);

            channel.DidNotReceiveWithAnyArgs();

        }


        [Fact]
        public void initialize_with_no_auto_provision_or_auto_purge()
        {
            theTransport.AutoProvision = false;
            theTransport.AutoPurgeAllQueues = false;

            var exchange = new RabbitMqExchange("bar", theTransport);
            exchange.Initialize(theChannel, NullLogger.Instance);
            
            theTransport.Queues["foo"].PurgeOnStartup = false;
            
            
            theChannel.DidNotReceiveWithAnyArgs().ExchangeDeclare("bar", "fanout", true, false, null);
        }


        [Fact]
        public void initialize_with_no_auto_provision_but_auto_purge_on_endpoint_only()
        {
            theTransport.AutoProvision = false;
            theTransport.AutoPurgeAllQueues = false;

            var endpoint = new RabbitMqExchange("bar", theTransport);


            endpoint.Initialize(theChannel, NullLogger.Instance);

            theChannel.DidNotReceiveWithAnyArgs().ExchangeDeclare("bar", "fanout", true, false, null);
        }

        [Fact]
        public void initialize_with_no_auto_provision_but_global_auto_purge()
        {
            theTransport.AutoProvision = false;
            theTransport.AutoPurgeAllQueues = true;

            var endpoint = new RabbitMqExchange("bar",theTransport);

            endpoint.Initialize(theChannel, NullLogger.Instance);

            theChannel.DidNotReceiveWithAnyArgs().ExchangeDeclare("bar", "fanout", true, false, null);
        }

        [Fact]
        public void initialize_with_auto_provision_and_global_auto_purge()
        {
            theTransport.AutoProvision = true;

            var endpoint = new RabbitMqExchange("bar", theTransport);


            endpoint.Initialize(theChannel, NullLogger.Instance);

            theChannel.Received().ExchangeDeclare("bar", "fanout", endpoint.IsDurable, endpoint.AutoDelete, endpoint.Arguments);
        }

        [Fact]
        public void initialize_with_auto_provision_and_local_auto_purge()
        {
            theTransport.AutoProvision = true;

            var endpoint = new RabbitMqExchange("bar",theTransport);


            endpoint.Initialize(theChannel, NullLogger.Instance);

            theChannel.Received().ExchangeDeclare("bar", "fanout", endpoint.IsDurable, endpoint.AutoDelete, endpoint.Arguments);
        }

    }
}
