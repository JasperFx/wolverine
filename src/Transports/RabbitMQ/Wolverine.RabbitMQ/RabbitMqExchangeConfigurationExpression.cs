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
    public bool DeclarePassive
    {
        get => _exchange.DeclarePassive;
        set => _exchange.DeclarePassive = value;
    }

    /// <summary>
    /// When <c>true</c>, Wolverine treats this exchange as owned by an external system: it will not
    /// declare (create) it at startup or delete it during <c>resources teardown</c>, even when
    /// <c>AutoProvision()</c> is enabled, and will not set up or tear down its bindings. Use this when
    /// the calling identity lacks the <c>configure</c>/<c>delete</c> permissions for the exchange.
    /// Distinct from <see cref="DeclarePassive"/>, which still touches the broker to verify existence
    /// at startup. See https://github.com/JasperFx/wolverine/issues/3064.
    /// </summary>
    public bool IsExternallyOwned
    {
        get => _exchange.IsExternallyOwned;
        set => _exchange.IsExternallyOwned = value;
    }

    public IDictionary<string, object?> Arguments => _exchange.Arguments;
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

    public RabbitMqExchangeBinding BindExchange(string sourceExchangeName, string? bindingKey = null)
    {
        return _exchange.BindExchange(sourceExchangeName, bindingKey);
    }
}