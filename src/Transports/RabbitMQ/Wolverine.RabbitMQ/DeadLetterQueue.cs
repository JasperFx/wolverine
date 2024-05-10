using Wolverine.RabbitMQ.Internal;

namespace Wolverine.RabbitMQ;

public enum DeadLetterQueueMode
{
    /// <summary>
    /// Opt into using Rabbit MQ's native dead letter queue approach. This is the default and recommended
    /// </summary>
    Native,

    /// <summary>
    /// When interoperating with some other messaging tools that do not support Rabbit MQ's dead letter queueing functionality
    /// or do so differently than Wolverine, it may be necessary to use this option to enable Wolverine to move messages
    /// to the dead letter queue in a programmatic way that will not interfere with other messaging tools
    /// </summary>
    InteropFriendly,

    /// <summary>
    /// Completely ignore Rabbit MQ native dead letter queueing in favor of Wolverine persistent dead letter queueing
    /// </summary>
    WolverineStorage
}

public class DeadLetterQueue
{
    private string? _exchangeName;
    private string _queueName = RabbitMqTransport.DeadLetterQueueName;
    private string? _bindingName;

    public DeadLetterQueueMode Mode { get; set; } = DeadLetterQueueMode.Native;

    public DeadLetterQueue(string queueName)
    {
        _queueName = queueName;
    }

    public DeadLetterQueue(string queueName, DeadLetterQueueMode mode)
    {
        _queueName = queueName;
        Mode = mode;
    }

    public string ExchangeName
    {
        get => _exchangeName ?? _queueName;
        set => _exchangeName = value ?? throw new ArgumentNullException(nameof(ExchangeName));
    }

    public string QueueName
    {
        get => _queueName;
        set => _queueName = value?? throw new ArgumentNullException(nameof(QueueName));
    }

    public string? BindingName
    {
        get => _bindingName ?? _queueName;
        set => _bindingName = value;
    }

    public Action<RabbitMqQueue>? ConfigureQueue { get; set; }

    public Action<RabbitMqExchange>? ConfigureExchange { get; set; }

    protected bool Equals(DeadLetterQueue other)
    {
        return _queueName == other._queueName && ExchangeName == other.ExchangeName;
    }

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(null, obj))
        {
            return false;
        }

        if (ReferenceEquals(this, obj))
        {
            return true;
        }

        if (obj.GetType() != this.GetType())
        {
            return false;
        }

        return Equals((DeadLetterQueue)obj);
    }

    public override int GetHashCode()
    {
        return _queueName.GetHashCode();
    }
}