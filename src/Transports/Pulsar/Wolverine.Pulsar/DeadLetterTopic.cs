namespace Wolverine.Pulsar;

public enum DeadLetterTopicMode
{
    /// <summary>
    /// Opt into using Pulsar's native dead letter topic approach. This is the default and recommended
    /// </summary>
    Native,

    /// <summary>
    /// Completely ignore Pulsar native dead letter topic in favor of Wolverine persistent dead letter queueing
    /// </summary>
    WolverineStorage
}

public class DeadLetterTopic
{

    public static DeadLetterTopic DefaultNative => new(DeadLetterTopicMode.Native);

    private string? _topicName;

    public DeadLetterTopicMode Mode { get; set; }

    public DeadLetterTopic(DeadLetterTopicMode mode)
    {
        Mode = mode;
    }

    public DeadLetterTopic(string topicName, DeadLetterTopicMode mode)
    {
        _topicName = topicName;
        Mode = mode;
    }

    public string? TopicName
    {
        get => _topicName;
        set => _topicName = value ?? throw new ArgumentNullException(nameof(TopicName));
    }

    protected bool Equals(DeadLetterTopic other)
    {
        return _topicName == other._topicName;
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

        return Equals((DeadLetterTopic)obj);
    }

    public override int GetHashCode()
    {
        return _topicName?.GetHashCode() ?? 0;
    }
}