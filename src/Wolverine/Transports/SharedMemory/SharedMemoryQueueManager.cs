using JasperFx.Core;

namespace Wolverine.Transports.SharedMemory;

public static class SharedMemoryQueueManager
{
    public static readonly Cache<string, Topic> Topics = new(topicName => new Topic(topicName));
    
    public static ValueTask PostAsync(Envelope envelope)
    {
        if (envelope.TopicName.IsEmpty())
        {
            throw new ArgumentOutOfRangeException(nameof(envelope), "Envelope has no topic");
        }

        return Topics[envelope.TopicName].PostAsync(envelope);
    }

    public static async Task WaitForCompletionAsync()
    {
        foreach (var topic in Topics)
        {
            await topic.WaitForCompletionAsync();
        }
    }

    public static async Task ClearAllAsync()
    {
        foreach (var topic in Topics)
        {
            await topic.DisposeAsync();
        }
        
        Topics.ClearAll();
    }
}