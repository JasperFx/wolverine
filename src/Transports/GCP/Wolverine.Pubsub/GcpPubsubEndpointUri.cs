namespace Wolverine.Pubsub;

/// <summary>
/// Builds canonical Wolverine endpoint <see cref="Uri"/> values for Google Cloud Pub/Sub transport endpoints.
/// </summary>
public static class GcpPubsubEndpointUri
{
    /// <summary>
    /// Builds a URI referencing a Google Cloud Pub/Sub topic endpoint in the canonical form
    /// <c>pubsub://{projectId}/{topicName}</c>.
    /// </summary>
    /// <param name="projectId">The GCP project ID that owns the topic.</param>
    /// <param name="topicName">The Pub/Sub topic name.</param>
    /// <returns>A <see cref="Uri"/> of the form <c>pubsub://{projectId}/{topicName}</c>.</returns>
    /// <example><c>GcpPubsubEndpointUri.Topic("my-project", "orders")</c> returns <c>pubsub://my-project/orders</c>.</example>
    /// <exception cref="ArgumentException">Thrown when either parameter is null, empty, or whitespace.</exception>
    public static Uri Topic(string projectId, string topicName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(topicName);
        return new Uri($"pubsub://{projectId}/{topicName}");
    }
}
