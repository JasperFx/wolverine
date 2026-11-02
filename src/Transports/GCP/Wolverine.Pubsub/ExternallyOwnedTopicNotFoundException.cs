namespace Wolverine.Pubsub;

/// <summary>
///     GH-5342. Thrown while provisioning a subscription whose topic is marked externally owned, when that topic
///     does not exist yet. Wolverine will not create a topic it does not own, so the subscription cannot be created
///     until the topic's owner creates it.
/// </summary>
public class ExternallyOwnedTopicNotFoundException : Exception
{
    public ExternallyOwnedTopicNotFoundException(string topicName, string subscriptionName) : base(
        $"Unable to create the Google Cloud Platform Pub/Sub subscription '{subscriptionName}' because its topic '{topicName}' does not exist. The topic is marked as externally owned, so Wolverine will not create it. It has to be created by whoever owns it first.")
    {
        TopicName = topicName;
        SubscriptionName = subscriptionName;
    }

    public string TopicName { get; }
    public string SubscriptionName { get; }
}
