using Wolverine.RabbitMQ.Internal;

namespace Wolverine.RabbitMQ;

public sealed class TopicBindingExchange
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