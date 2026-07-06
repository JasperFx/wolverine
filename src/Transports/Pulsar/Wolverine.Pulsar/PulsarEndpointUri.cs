namespace Wolverine.Pulsar;

/// <summary>
/// Builds canonical Wolverine endpoint <see cref="Uri"/> values for Pulsar transport endpoints.
/// All methods return Wolverine endpoint URIs of the form <c>pulsar://persistent/{tenant}/{ns}/{topic}</c>
/// or <c>pulsar://non-persistent/{tenant}/{ns}/{topic}</c>, matching what <see cref="PulsarEndpoint"/>'s parser
/// accepts. For Pulsar-native topic-path strings (e.g. <c>persistent://...</c>) used by the native Pulsar client,
/// build them directly — they are not Wolverine endpoint URIs and are out of scope for this helper.
/// </summary>
public static class PulsarEndpointUri
{
    /// <summary>
    /// Builds a URI referencing a Pulsar persistent-topic endpoint in the canonical form
    /// <c>pulsar://persistent/{tenant}/{namespace}/{topicName}</c>.
    /// </summary>
    /// <param name="tenant">The Pulsar tenant.</param>
    /// <param name="namespace">The Pulsar namespace.</param>
    /// <param name="topicName">The Pulsar topic name.</param>
    /// <returns>A <see cref="Uri"/> of the form <c>pulsar://persistent/{tenant}/{namespace}/{topicName}</c>.</returns>
    /// <example><c>PulsarEndpointUri.PersistentTopic("public", "default", "orders")</c> returns <c>pulsar://persistent/public/default/orders</c>.</example>
    /// <exception cref="ArgumentException">Thrown when any parameter is null, empty, or whitespace.</exception>
    public static Uri PersistentTopic(string tenant, string @namespace, string topicName)
    {
        return BuildEndpointUri(PulsarEndpoint.Persistent, tenant, @namespace, topicName);
    }

    /// <summary>
    /// Builds a URI referencing a Pulsar non-persistent-topic endpoint in the canonical form
    /// <c>pulsar://non-persistent/{tenant}/{namespace}/{topicName}</c>.
    /// </summary>
    /// <param name="tenant">The Pulsar tenant.</param>
    /// <param name="namespace">The Pulsar namespace.</param>
    /// <param name="topicName">The Pulsar topic name.</param>
    /// <returns>A <see cref="Uri"/> of the form <c>pulsar://non-persistent/{tenant}/{namespace}/{topicName}</c>.</returns>
    /// <example><c>PulsarEndpointUri.NonPersistentTopic("public", "default", "orders")</c> returns <c>pulsar://non-persistent/public/default/orders</c>.</example>
    /// <exception cref="ArgumentException">Thrown when any parameter is null, empty, or whitespace.</exception>
    public static Uri NonPersistentTopic(string tenant, string @namespace, string topicName)
    {
        return BuildEndpointUri(PulsarEndpoint.NonPersistent, tenant, @namespace, topicName);
    }

    /// <summary>
    /// Builds a URI referencing a Pulsar topic endpoint, selecting persistent or non-persistent by flag.
    /// </summary>
    /// <param name="tenant">The Pulsar tenant.</param>
    /// <param name="namespace">The Pulsar namespace.</param>
    /// <param name="topicName">The Pulsar topic name.</param>
    /// <param name="persistent"><c>true</c> for a persistent topic, <c>false</c> for non-persistent.</param>
    /// <returns>A <see cref="Uri"/> of the form <c>pulsar://persistent/...</c> or <c>pulsar://non-persistent/...</c>.</returns>
    /// <example><c>PulsarEndpointUri.Topic("public", "default", "orders", persistent: true)</c> returns <c>pulsar://persistent/public/default/orders</c>.</example>
    /// <exception cref="ArgumentException">Thrown when any string parameter is null, empty, or whitespace.</exception>
    public static Uri Topic(string tenant, string @namespace, string topicName, bool persistent)
    {
        var scheme = persistent ? PulsarEndpoint.Persistent : PulsarEndpoint.NonPersistent;
        return BuildEndpointUri(scheme, tenant, @namespace, topicName);
    }

    /// <summary>
    /// Converts a Pulsar-native topic path (e.g. <c>persistent://tenant/ns/topic</c>) into a
    /// Wolverine endpoint URI of the form <c>pulsar://persistent/tenant/ns/topic</c>.
    /// </summary>
    /// <param name="topicPath">A Pulsar-native topic path string with scheme <c>persistent</c> or <c>non-persistent</c> and exactly three path components (tenant, namespace, topic).</param>
    /// <returns>A <see cref="Uri"/> of the form <c>pulsar://{persistence}/{tenant}/{namespace}/{topicName}</c>.</returns>
    /// <example><c>PulsarEndpointUri.Topic("persistent://t1/ns1/aaa")</c> returns <c>pulsar://persistent/t1/ns1/aaa</c>.</example>
    /// <exception cref="ArgumentException">Thrown when <paramref name="topicPath"/> is null, empty, whitespace, uses a scheme other than <c>persistent</c>/<c>non-persistent</c>, or does not contain exactly tenant/namespace/topic path parts.</exception>
    /// <exception cref="UriFormatException">Thrown when <paramref name="topicPath"/> is not a parseable URI.</exception>
    public static Uri Topic(string topicPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topicPath);

        var parsed = new Uri(topicPath);

        if (parsed.Scheme != PulsarEndpoint.Persistent && parsed.Scheme != PulsarEndpoint.NonPersistent)
        {
            throw new ArgumentException(
                $"topicPath must use scheme '{PulsarEndpoint.Persistent}' or '{PulsarEndpoint.NonPersistent}', got '{parsed.Scheme}'. Use PulsarEndpointUri.PersistentTopic or NonPersistentTopic to build Wolverine endpoint URIs from components.",
                nameof(topicPath));
        }

        var pathParts = parsed.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (pathParts.Length != 2)
        {
            throw new ArgumentException(
                $"topicPath must have the form '{{scheme}}://{{tenant}}/{{namespace}}/{{topic}}', got '{topicPath}'.",
                nameof(topicPath));
        }

        return parsed.Scheme == PulsarEndpoint.Persistent
            ? PersistentTopic(parsed.Host, pathParts[0], pathParts[1])
            : NonPersistentTopic(parsed.Host, pathParts[0], pathParts[1]);
    }

    /// <summary>
    /// Converts a Pulsar-native topic path into a Wolverine endpoint URI whose scheme is
    /// <paramref name="scheme"/> (the owning transport's <see cref="PulsarTransport.Protocol"/>) rather than the
    /// hard-coded <c>pulsar</c>. For the default broker <paramref name="scheme"/> is <c>pulsar</c>; for a named
    /// broker (<see cref="PulsarTransportExtensions.AddNamedPulsarBroker"/>) it is the broker name, so the named
    /// broker's endpoints route through <c>ForScheme</c> and never collide with the default broker's endpoints.
    /// <see cref="PulsarEndpoint.Parse"/> only validates the host segment, so the scheme is free to vary per broker.
    /// </summary>
    internal static Uri Topic(string topicPath, string scheme)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topicPath);

        var parsed = new Uri(topicPath);

        if (parsed.Scheme != PulsarEndpoint.Persistent && parsed.Scheme != PulsarEndpoint.NonPersistent)
        {
            throw new ArgumentException(
                $"topicPath must use scheme '{PulsarEndpoint.Persistent}' or '{PulsarEndpoint.NonPersistent}', got '{parsed.Scheme}'.",
                nameof(topicPath));
        }

        var pathParts = parsed.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (pathParts.Length != 2)
        {
            throw new ArgumentException(
                $"topicPath must have the form '{{scheme}}://{{tenant}}/{{namespace}}/{{topic}}', got '{topicPath}'.",
                nameof(topicPath));
        }

        // parsed.Scheme is the persistence segment ("persistent"/"non-persistent"); parsed.Host is the native
        // Pulsar tenant. The resulting Wolverine endpoint URI is "{scheme}://{persistence}/{tenant}/{ns}/{topic}".
        return BuildEndpointUri(scheme, parsed.Scheme, parsed.Host, pathParts[0], pathParts[1]);
    }

    private static Uri BuildEndpointUri(string persistence, string tenant, string @namespace, string topicName)
    {
        return BuildEndpointUri(PulsarTransport.ProtocolName, persistence, tenant, @namespace, topicName);
    }

    private static Uri BuildEndpointUri(string scheme, string persistence, string tenant, string @namespace,
        string topicName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant);
        ArgumentException.ThrowIfNullOrWhiteSpace(@namespace);
        ArgumentException.ThrowIfNullOrWhiteSpace(topicName);
        return new Uri($"{scheme}://{persistence}/{tenant}/{@namespace}/{topicName}");
    }
}
