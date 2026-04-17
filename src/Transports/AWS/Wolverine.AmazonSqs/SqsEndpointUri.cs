namespace Wolverine.AmazonSqs;

/// <summary>
/// Builds canonical Wolverine endpoint <see cref="Uri"/> values for AWS SQS transport endpoints.
/// </summary>
public static class SqsEndpointUri
{
    /// <summary>
    /// Builds a URI referencing an SQS queue endpoint in the canonical form
    /// <c>sqs://{queueName}</c>. FIFO queue names (with <c>.fifo</c> suffix) are preserved verbatim.
    /// </summary>
    /// <param name="queueName">The SQS queue name.</param>
    /// <returns>A <see cref="Uri"/> of the form <c>sqs://{queueName}</c>.</returns>
    /// <example><c>SqsEndpointUri.Queue("orders")</c> returns <c>sqs://orders</c>.</example>
    /// <exception cref="ArgumentException">Thrown when <paramref name="queueName"/> is null, empty, or whitespace.</exception>
    public static Uri Queue(string queueName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queueName);
        return new Uri($"sqs://{queueName}");
    }
}
