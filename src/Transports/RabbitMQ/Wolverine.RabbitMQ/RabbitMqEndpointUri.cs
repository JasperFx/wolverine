namespace Wolverine.RabbitMQ;

/// <summary>
/// Builds canonical Wolverine endpoint <see cref="Uri"/> values for RabbitMQ transport endpoints.
/// </summary>
public static class RabbitMqEndpointUri
{
    /// <summary>
    /// Builds a URI referencing a RabbitMQ queue endpoint in the canonical form
    /// <c>rabbitmq://queue/{queueName}</c>.
    /// </summary>
    /// <param name="queueName">The RabbitMQ queue name.</param>
    /// <returns>A <see cref="Uri"/> of the form <c>rabbitmq://queue/{queueName}</c>.</returns>
    /// <example><c>RabbitMqEndpointUri.Queue("orders")</c> returns <c>rabbitmq://queue/orders</c>.</example>
    /// <exception cref="ArgumentException">Thrown when <paramref name="queueName"/> is null, empty, or whitespace.</exception>
    public static Uri Queue(string queueName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queueName);
        return new Uri($"rabbitmq://queue/{queueName}");
    }

    /// <summary>
    /// Builds a URI referencing a RabbitMQ exchange endpoint in the canonical form
    /// <c>rabbitmq://exchange/{exchangeName}</c>.
    /// </summary>
    /// <param name="exchangeName">The RabbitMQ exchange name.</param>
    /// <returns>A <see cref="Uri"/> of the form <c>rabbitmq://exchange/{exchangeName}</c>.</returns>
    /// <example><c>RabbitMqEndpointUri.Exchange("events")</c> returns <c>rabbitmq://exchange/events</c>.</example>
    /// <exception cref="ArgumentException">Thrown when <paramref name="exchangeName"/> is null, empty, or whitespace.</exception>
    public static Uri Exchange(string exchangeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exchangeName);
        return new Uri($"rabbitmq://exchange/{exchangeName}");
    }

    /// <summary>
    /// Builds a URI referencing a RabbitMQ topic-routed exchange endpoint in the canonical form
    /// <c>rabbitmq://topic/{exchangeName}/{routingKey}</c>.
    /// </summary>
    /// <param name="exchangeName">The RabbitMQ exchange name.</param>
    /// <param name="routingKey">The topic routing key.</param>
    /// <returns>A <see cref="Uri"/> of the form <c>rabbitmq://topic/{exchangeName}/{routingKey}</c>.</returns>
    /// <example><c>RabbitMqEndpointUri.Topic("prices", "usd.eur")</c> returns <c>rabbitmq://topic/prices/usd.eur</c>.</example>
    /// <exception cref="ArgumentException">Thrown when either parameter is null, empty, or whitespace.</exception>
    public static Uri Topic(string exchangeName, string routingKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exchangeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(routingKey);
        return new Uri($"rabbitmq://topic/{exchangeName}/{routingKey}");
    }

    /// <summary>
    /// Builds a URI referencing a RabbitMQ exchange with an explicit routing key in the canonical form
    /// <c>rabbitmq://exchange/{exchangeName}/routing/{routingKey}</c>.
    /// </summary>
    /// <param name="exchangeName">The RabbitMQ exchange name.</param>
    /// <param name="routingKey">The routing key bound to the exchange.</param>
    /// <returns>A <see cref="Uri"/> of the form <c>rabbitmq://exchange/{exchangeName}/routing/{routingKey}</c>.</returns>
    /// <example><c>RabbitMqEndpointUri.Routing("events", "order.created")</c> returns <c>rabbitmq://exchange/events/routing/order.created</c>.</example>
    /// <exception cref="ArgumentException">Thrown when either parameter is null, empty, or whitespace.</exception>
    public static Uri Routing(string exchangeName, string routingKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exchangeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(routingKey);
        return new Uri($"rabbitmq://exchange/{exchangeName}/routing/{routingKey}");
    }
}
