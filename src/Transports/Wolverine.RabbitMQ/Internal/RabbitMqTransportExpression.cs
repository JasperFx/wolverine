using System;
using System.Collections.Generic;
using RabbitMQ.Client;
using Wolverine.Transports;

namespace Wolverine.RabbitMQ.Internal;

public class RabbitMqTransportExpression : BrokerExpression<RabbitMqTransport, RabbitMqQueue, RabbitMqEndpoint,
    RabbitMqListenerConfiguration, RabbitMqSubscriberConfiguration, RabbitMqTransportExpression>
{
    public RabbitMqTransportExpression(RabbitMqTransport transport, WolverineOptions options) : base(transport, options)
    {
    }

    /// <summary>
    /// Make any necessary customizations to the Rabbit MQ client's ConnectionFactory 
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public RabbitMqTransportExpression ConfigureConnection(Action<ConnectionFactory> configure)
    {
        if (configure == null)
        {
            throw new ArgumentNullException(nameof(configure));
        }

        configure(Transport.ConnectionFactory);

        return this;
    }

    protected override RabbitMqListenerConfiguration createListenerExpression(RabbitMqQueue listenerEndpoint)
    {
        return new RabbitMqListenerConfiguration(listenerEndpoint);
    }

    protected override RabbitMqSubscriberConfiguration createSubscriberExpression(RabbitMqEndpoint subscriberEndpoint)
    {
        return new RabbitMqSubscriberConfiguration(subscriberEndpoint);
    }

    public BindingExpression BindExchange(string exchangeName, Action<IRabbitMqExchange>? configure = null)
    {
        DeclareExchange(exchangeName, configure);
        return new BindingExpression(exchangeName, this);
    }


    /// <summary>
    ///     Opt into using conventional Rabbit MQ routing based on the message types
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public RabbitMqTransportExpression UseConventionalRouting(
        Action<RabbitMqMessageRoutingConvention>? configure = null)
    {
        var convention = new RabbitMqMessageRoutingConvention();
        configure?.Invoke(convention);
        Options.RoutingConventions.Add(convention);

        return this;
    }

    /// <summary>
    ///     Declare a new exchange without impacting message routing or listening. The default exchange type is "fan out". This
    ///     does not respect identifier prefixes!
    /// </summary>
    /// <param name="exchangeName"></param>
    /// <param name="configure"></param>
    public RabbitMqTransportExpression DeclareExchange(string exchangeName,
        Action<IRabbitMqExchange>? configure = null)
    {
        var exchange = Transport.Exchanges[exchangeName];
        configure?.Invoke(exchange);

        return this;
    }

    /// <summary>
    ///     Declare a binding from a Rabbit Mq exchange to a Rabbit MQ queue. This does not respect identifier prefixes!
    /// </summary>
    /// <param name="exchangeName"></param>
    /// <returns></returns>
    public BindingExpression BindExchange(string exchangeName, ExchangeType exchangeType)
    {
        return BindExchange(exchangeName, e => e.ExchangeType = exchangeType);
    }

    /// <summary>
    ///     Declare that a queue should be created with the supplied name and optional configuration. . This does not respect
    ///     identifier prefixes!
    /// </summary>
    /// <param name="queueName"></param>
    /// <param name="configure"></param>
    public RabbitMqTransportExpression DeclareQueue(string queueName, Action<IRabbitMqQueue>? configure = null)
    {
        var queue = Transport.Queues[queueName];
        configure?.Invoke(queue);

        return this;
    }

    public class BindingExpression
    {
        private readonly string _exchangeName;
        private readonly RabbitMqTransportExpression _parent;

        internal BindingExpression(string exchangeName, RabbitMqTransportExpression parent)
        {
            _exchangeName = exchangeName;
            _parent = parent;
        }

        /// <summary>
        ///     Bind the named exchange to a queue. The routing key will be
        ///     [exchange name]_[queue name]
        /// </summary>
        /// <param name="queueName"></param>
        /// <param name="configure">Optional configuration of the Rabbit MQ queue</param>
        public RabbitMqTransportExpression ToQueue(string queueName, Action<IRabbitMqQueue>? configure = null)
        {
            _parent.DeclareQueue(queueName, configure);
            ToQueue(queueName, $"{_exchangeName}_{queueName}");

            return _parent;
        }

        /// <summary>
        ///     Bind the named exchange to a queue with a user supplied binding key
        /// </summary>
        /// <param name="queueName"></param>
        /// <param name="bindingKey"></param>
        /// <param name="configure">Optional configuration of the Rabbit MQ queue</param>
        /// <param name="arguments">Optional configuration for arguments to the Rabbit MQ binding</param>
        public RabbitMqTransportExpression ToQueue(string queueName, string bindingKey,
            Action<IRabbitMqQueue>? configure = null, Dictionary<string, object>? arguments = null)
        {
            _parent.DeclareQueue(queueName, configure);

            var binding = _parent.Transport.Exchanges[_exchangeName].BindQueue(queueName, bindingKey);

            if (arguments != null)
            {
                foreach (var argument in arguments) binding.Arguments[argument.Key] = argument.Value;
            }

            return _parent;
        }
    }
}