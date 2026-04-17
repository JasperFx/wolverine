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
        ArgumentException.ThrowIfNullOrWhiteSpace(topicName);
        return new Uri("mqtt://topic/" + topicName.Trim('/'));
    }
}
