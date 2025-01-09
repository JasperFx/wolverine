namespace Wolverine.Attributes;

/// <summary>
///     Directs Wolverine to send this to the named local queue when
///     using IMessageContext.Enqueue(message)
/// </summary>
[Obsolete("Prefer the StickyHandler attribute instead. This will be removed in Wolverine 4")]
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public class LocalQueueAttribute : Attribute
{
    public LocalQueueAttribute(string queueName)
    {
        QueueName = queueName;
    }

    public string QueueName { get; }
}