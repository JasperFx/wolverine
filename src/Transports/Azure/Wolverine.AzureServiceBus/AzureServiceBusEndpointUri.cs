namespace Wolverine.AzureServiceBus;

/// <summary>
/// Builds canonical Wolverine endpoint <see cref="Uri"/> values for Azure Service Bus transport endpoints.
/// </summary>
public static class AzureServiceBusEndpointUri
{
    /// <summary>
    /// Builds a URI referencing an Azure Service Bus queue endpoint in the canonical form
    /// <c>asb://queue/{queueName}</c>.
    /// </summary>
    /// <param name="queueName">The Azure Service Bus queue name.</param>
    /// <returns>A <see cref="Uri"/> of the form <c>asb://queue/{queueName}</c>.</returns>
    /// <example><c>AzureServiceBusEndpointUri.Queue("orders")</c> returns <c>asb://queue/orders</c>.</example>
    /// <exception cref="ArgumentException">Thrown when <paramref name="queueName"/> is null, empty, or whitespace.</exception>
    public static Uri Queue(string queueName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queueName);
        return new Uri($"asb://queue/{queueName}");
    }

    /// <summary>
    /// Builds a URI referencing an Azure Service Bus topic endpoint in the canonical form
    /// <c>asb://topic/{topicName}</c>.
    /// </summary>
    /// <param name="topicName">The Azure Service Bus topic name.</param>
    /// <returns>A <see cref="Uri"/> of the form <c>asb://topic/{topicName}</c>.</returns>
    /// <example><c>AzureServiceBusEndpointUri.Topic("events")</c> returns <c>asb://topic/events</c>.</example>
    /// <exception cref="ArgumentException">Thrown when <paramref name="topicName"/> is null, empty, or whitespace.</exception>
    public static Uri Topic(string topicName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topicName);
        return new Uri($"asb://topic/{topicName}");
    }

    /// <summary>
    /// Builds a URI referencing an Azure Service Bus topic subscription endpoint in the canonical form
    /// <c>asb://topic/{topicName}/{subscriptionName}</c>.
    /// </summary>
    /// <param name="topicName">The Azure Service Bus topic name.</param>
    /// <param name="subscriptionName">The subscription name bound to the topic.</param>
    /// <returns>A <see cref="Uri"/> of the form <c>asb://topic/{topicName}/{subscriptionName}</c>.</returns>
    /// <example><c>AzureServiceBusEndpointUri.Subscription("events", "audit")</c> returns <c>asb://topic/events/audit</c>.</example>
    /// <exception cref="ArgumentException">Thrown when either parameter is null, empty, or whitespace.</exception>
    public static Uri Subscription(string topicName, string subscriptionName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topicName);
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionName);
        return new Uri($"asb://topic/{topicName}/{subscriptionName}");
    }
}
