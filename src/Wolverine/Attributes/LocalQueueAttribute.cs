using System;

namespace Wolverine.Attributes;

/// <summary>
///     Directs Wolverine to send this to the named local queue when
///     using IMessageContext.Enqueue(message)
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public class LocalQueueAttribute : Attribute
{
    public LocalQueueAttribute(string queueName)
    {
        QueueName = queueName;
    }

    public string QueueName { get; }
}