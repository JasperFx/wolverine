namespace Wolverine.Nats;

/// <summary>
/// Builds canonical Wolverine endpoint <see cref="Uri"/> values for NATS transport endpoints.
/// </summary>
public static class NatsEndpointUri
{
    /// <summary>
    /// Builds a URI referencing a NATS subject endpoint in the canonical form
    /// <c>nats://subject/{subject}</c>.
    /// </summary>
    /// <param name="subject">The NATS subject (dot-separated identifier).</param>
    /// <returns>A <see cref="Uri"/> of the form <c>nats://subject/{subject}</c>.</returns>
    /// <example><c>NatsEndpointUri.Subject("orders.created")</c> returns <c>nats://subject/orders.created</c>.</example>
    /// <exception cref="ArgumentException">Thrown when <paramref name="subject"/> is null, empty, or whitespace.</exception>
    public static Uri Subject(string subject)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        return new Uri($"nats://subject/{subject}");
    }
}
