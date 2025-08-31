namespace Wolverine.RabbitMQ.Internal;

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

        // Just to make sure that the exchange exists so resource setup
        // works correctly
        _parent.Exchanges.FillDefault(exchangeName);
        
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