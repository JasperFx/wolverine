namespace Wolverine.MQTT;

/// <summary>
/// Builds canonical Wolverine endpoint <see cref="Uri"/> values for MQTT transport endpoints.
/// </summary>
public static class MqttEndpointUri
{
    /// <summary>
    /// Builds a URI referencing an MQTT topic endpoint in the canonical form
    /// <c>mqtt://topic/{topicName}</c>. Slashes inside the topic name are preserved
    /// as path separators.
    /// </summary>
    /// <param name="topicName">The MQTT topic name (may contain slashes).</param>
    /// <returns>A <see cref="Uri"/> of the form <c>mqtt://topic/{topicName}</c>.</returns>
    /// <example><c>MqttEndpointUri.Topic("sensor/temperature")</c> returns <c>mqtt://topic/sensor/temperature</c>.</example>
    /// <exception cref="ArgumentException">Thrown when <paramref name="topicName"/> is null, empty, or whitespace.</exception>
    public static Uri Topic(string topicName)
    {
        return Topic("mqtt", topicName);
    }

    /// <summary>
    /// Builds a URI referencing an MQTT topic endpoint in the canonical form
    /// <c>{protocol}://topic/{topicName}</c>. The <paramref name="protocol"/> is the transport's URI scheme,
    /// which for a named broker (see <c>AddNamedMqttBroker</c>) is the broker name rather than <c>mqtt</c>.
    /// Slashes inside the topic name are preserved as path separators.
    /// </summary>
    /// <param name="protocol">The transport's URI scheme (e.g. <c>mqtt</c> or a named broker's name).</param>
    /// <param name="topicName">The MQTT topic name (may contain slashes).</param>
    /// <returns>A <see cref="Uri"/> of the form <c>{protocol}://topic/{topicName}</c>.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="protocol"/> or <paramref name="topicName"/> is null, empty, or whitespace.</exception>
    public static Uri Topic(string protocol, string topicName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(protocol);
        ArgumentException.ThrowIfNullOrWhiteSpace(topicName);
        return new Uri($"{protocol}://topic/" + topicName.Trim('/'));
    }
}
