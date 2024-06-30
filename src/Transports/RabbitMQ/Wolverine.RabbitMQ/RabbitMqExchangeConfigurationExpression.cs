namespace Wolverine.RabbitMQ.Internal;

public sealed class RabbitMqExchangeConfigurationExpression : IRabbitMqBindableExchange
{
    private readonly RabbitMqExchange _exchange;
    private readonly RabbitMqTransport _transport;

    public RabbitMqExchangeConfigurationExpression(string exchangeName, RabbitMqTransport transport)
    {
        _exchange = transport.Exchanges[exchangeName];
        _transport = transport;
    }

    public string Name => _exchange.Name;

    public bool IsDurable
    {
        get => _exchange.IsDurable;
        set => _exchange.IsDurable = value;
    }
    public ExchangeType ExchangeType
    {
        get => _exchange.ExchangeType;
        set => _exchange.ExchangeType = value;
    }
    public bool AutoDelete
    {
        get => _exchange.AutoDelete;
        set => _exchange.AutoDelete = value;
    }
    public IDictionary<string, object> Arguments => _exchange.Arguments;
    public TopicBindingExchange BindTopic(string topicPattern)
    {
        var queue = _transport.Queues[Name];
        queue.BindTopic(topicPattern);
        return new TopicBindingExchange(_transport, topicPattern, Name);
    }

    public RabbitMqBinding BindQueue(string queueName, string? bindingKey = null)
    {
        var queue = _transport.Queues[queueName];
        return queue.BindExchange(Name, bindingKey);
    }
}