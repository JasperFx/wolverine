namespace Wolverine.Kafka;

/// <summary>
/// Builds canonical Wolverine endpoint <see cref="Uri"/> values for Kafka transport endpoints.
/// </summary>
public static class KafkaEndpointUri
{
    /// <summary>
    /// Builds a URI referencing a Kafka topic endpoint in the canonical form
    /// <c>kafka://topic/{topicName}</c>.
    /// </summary>
    /// <param name="topicName">The Kafka topic name.</param>
    /// <returns>A <see cref="Uri"/> of the form <c>kafka://topic/{topicName}</c>.</returns>
    /// <example><c>KafkaEndpointUri.Topic("orders")</c> returns <c>kafka://topic/orders</c>.</example>
    /// <exception cref="ArgumentException">Thrown when <paramref name="topicName"/> is null, empty, or whitespace.</exception>
    public static Uri Topic(string topicName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topicName);
        return new Uri($"kafka://topic/{topicName}");
    }
}
