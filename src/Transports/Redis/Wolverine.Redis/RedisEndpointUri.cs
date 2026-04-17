namespace Wolverine.Redis;

/// <summary>
/// Builds canonical Wolverine endpoint <see cref="Uri"/> values for Redis stream transport endpoints.
/// </summary>
public static class RedisEndpointUri
{
    /// <summary>
    /// Builds a URI referencing a Redis stream endpoint in the canonical form
    /// <c>redis://stream/{databaseId}/{streamKey}</c>.
    /// </summary>
    /// <param name="streamKey">The Redis stream key name.</param>
    /// <param name="databaseId">The Redis database ID (default 0).</param>
    /// <returns>A <see cref="Uri"/> of the form <c>redis://stream/{databaseId}/{streamKey}</c>.</returns>
    /// <example><c>RedisEndpointUri.Stream("orders", 3)</c> returns <c>redis://stream/3/orders</c>.</example>
    /// <exception cref="ArgumentException">Thrown when <paramref name="streamKey"/> is null, empty, or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="databaseId"/> is negative.</exception>
    public static Uri Stream(string streamKey, int databaseId = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamKey);
        ArgumentOutOfRangeException.ThrowIfNegative(databaseId);
        return new Uri($"redis://stream/{databaseId}/{streamKey}");
    }

    /// <summary>
    /// Builds a URI referencing a Redis stream endpoint with a consumer group in the canonical form
    /// <c>redis://stream/{databaseId}/{streamKey}?consumerGroup={consumerGroup}</c>.
    /// </summary>
    /// <param name="streamKey">The Redis stream key name.</param>
    /// <param name="databaseId">The Redis database ID.</param>
    /// <param name="consumerGroup">The Redis consumer group name.</param>
    /// <returns>A <see cref="Uri"/> of the form <c>redis://stream/{databaseId}/{streamKey}?consumerGroup={consumerGroup}</c>.</returns>
    /// <example><c>RedisEndpointUri.Stream("orders", 3, "order-processors")</c> returns <c>redis://stream/3/orders?consumerGroup=order-processors</c>.</example>
    /// <exception cref="ArgumentException">Thrown when <paramref name="streamKey"/> or <paramref name="consumerGroup"/> is null, empty, or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="databaseId"/> is negative.</exception>
    public static Uri Stream(string streamKey, int databaseId, string consumerGroup)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamKey);
        ArgumentOutOfRangeException.ThrowIfNegative(databaseId);
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerGroup);
        return new Uri($"redis://stream/{databaseId}/{streamKey}?consumerGroup={consumerGroup}");
    }
}
