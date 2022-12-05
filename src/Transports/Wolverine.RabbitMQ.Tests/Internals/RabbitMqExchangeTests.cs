using System;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using RabbitMQ.Client;
using Shouldly;
using Wolverine.RabbitMQ.Internal;
using Xunit;

namespace Wolverine.RabbitMQ.Tests.Internals;

public class configuration_model_specs
{
    private readonly IModel theChannel = Substitute.For<IModel>();

    private readonly RabbitMqTransport theTransport = new();

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
    public void default_endpoint_name_is_exchange_name()
    {
        var exchange = new RabbitMqExchange("foo", new RabbitMqTransport());
        exchange.EndpointName.ShouldBe(exchange.ExchangeName);
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
}