namespace Wolverine.RabbitMQ.Internal;

public class TopicBindingExchange
{
    private readonly RabbitMqTransport _transport;
    private readonly string _topicPattern;
    private readonly string _exchangeName;

    public TopicBindingExchange(RabbitMqTransport transport, string topicPattern, string exchangeName)
    {
        _transport = transport;
        _topicPattern = topicPattern;
        _exchangeName = exchangeName;
    }

    /// <summary>
    ///     Create a binding of the topic pattern previously specified to a Rabbit Mq queue
    /// </summary>
    /// <param name="queueName">The name of the Rabbit Mq queue</param>
    /// <param name="configureQueue">Optionally configure </param>
    public void ToQueue(string queueName, Action<IRabbitMqQueue>? configureQueue = null)
    {
        var queue = _transport.Queues[queueName];
        var binding = queue.BindExchange(_exchangeName, _topicPattern);
        configureQueue?.Invoke(binding.Queue);
    }
}


// Legacy interface for exchange -> queue binding expressions
public interface IRabbitMqBindableExchange : IRabbitMqExchange
{
    public TopicBindingExchange BindTopic(string topicPattern);

    public RabbitMqBinding BindQueue(string queueName, string? bindingKey = null);
    
}

public class RabbitMqExchangeConfigurationExpression : IRabbitMqBindableExchange
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


public partial class RabbitMqQueue
{
    private readonly List<RabbitMqBinding> _bindings = [];
    
    internal bool HasBindings => _bindings.Count > 0;
    
    public RabbitMqBinding BindExchange(string exchangeName, string? bindingKey = null, Dictionary<string, object>? arguments = null)
    {
        if (exchangeName == null)
        {
            throw new ArgumentNullException(nameof(exchangeName));
        }

        var existing = _bindings.FirstOrDefault(x => x.ExchangeName == exchangeName && x.BindingKey == bindingKey);
        if (existing != null) return existing;
        
        var binding = new RabbitMqBinding(exchangeName, this, bindingKey);
        if (arguments is not null)
        {
            foreach (var argument in arguments)
            {
                binding.Arguments.Add(argument);
            }
        }
        _bindings.Add(binding);
        return binding;
    }
    
    public IEnumerable<RabbitMqBinding> Bindings()
    {
        return _bindings;
    }
    
    /// <summary>
    ///     Declare a Rabbit MQ binding with the supplied topic pattern to
    ///     the queue
    /// </summary>
    /// <param name="topicPattern"></param>
    /// <param name="bindingName"></param>
    /// <exception cref="NotImplementedException"></exception>
    public TopicBinding BindTopic(string topicPattern)
    {
        return new TopicBinding(this, topicPattern);
    }
    
    public class TopicBinding
    {
        private readonly RabbitMqQueue _queue;
        private readonly string _topicPattern;

        public TopicBinding(RabbitMqQueue queue, string topicPattern)
        {
            _queue = queue;
            _topicPattern = topicPattern;
        }

        /// <summary>
        ///     Create a binding of the topic pattern previously specified to a Rabbit Mq exchange
        /// </summary>
        /// <param name="queueName">The name of the Rabbit Mq queue</param>
        /// <param name="configureQueue">Optionally configure </param>
        public void ToExchange(string exchangeName, Dictionary<string, object>? arguments = null)
        {
            _queue.BindExchange(exchangeName, _topicPattern, arguments);
        }
    }

}