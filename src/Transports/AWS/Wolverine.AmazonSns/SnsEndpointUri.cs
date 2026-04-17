namespace Wolverine.AmazonSns;

/// <summary>
/// Builds canonical Wolverine endpoint <see cref="Uri"/> values for AWS SNS transport endpoints.
/// </summary>
public static class SnsEndpointUri
{
    /// <summary>
    /// Builds a URI referencing an SNS topic endpoint in the canonical form
    /// <c>sns://{topicName}</c>. FIFO topic names (with <c>.fifo</c> suffix) are preserved verbatim.
    /// </summary>
    /// <param name="topicName">The SNS topic name.</param>
    /// <returns>A <see cref="Uri"/> of the form <c>sns://{topicName}</c>.</returns>
    /// <example><c>SnsEndpointUri.Topic("events")</c> returns <c>sns://events</c>.</example>
    /// <exception cref="ArgumentException">Thrown when <paramref name="topicName"/> is null, empty, or whitespace.</exception>
    public static Uri Topic(string topicName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topicName);
        return new Uri($"sns://{topicName}");
    }
}
