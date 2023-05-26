using Wolverine.RabbitMQ.Internal;

namespace Wolverine.RabbitMQ;

public class DeadLetterQueue
{
    private string? _exchangeName;
    private string _queueName = RabbitMqTransport.DeadLetterQueueName;
    private string? _bindingName;

    public bool Enabled { get; set; } = true;

    public DeadLetterQueue(string queueName)
    {
        _queueName = queueName;
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