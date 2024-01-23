using System.Net.NetworkInformation;
using JasperFx.Core;
using Wolverine.MQTT.Internals;
using Wolverine.Runtime.Serialization;

namespace Wolverine.MQTT;

/// <summary>
/// Return as a cascading message to "zero out" the retained message in the MQTT topic
/// that the original message was received from
/// </summary>
public record AckMqttTopic() : ISendMyself, ISerializable
{
    public async ValueTask ApplyAsync(IMessageContext context)
    {
        var topicName = context.Envelope?.TopicName ?? MqttTransport.TopicForUri(context.Envelope?.Destination);
        if (topicName.IsEmpty())
        {
            throw new ArgumentOutOfRangeException(nameof(context), "Envelope.Topic cannot be empty or null");
        }

        await context.BroadcastToTopicAsync(topicName, this, new DeliveryOptions{ContentType = IntrinsicSerializer.MimeType});
    }

    public byte[] Write()
    {
        return Array.Empty<byte>();
    }

    public static object Read(byte[] bytes)
    {
        // It's never really used
        return new Ping();
    }
}

/// <summary>
/// Send this message to "clear out" an MQTT topic where the broker is
/// retaining messages
/// </summary>
/// <param name="TopicName"></param>
public record ClearMqttTopic(string TopicName) : ISendMyself, ISerializable
{
    public async ValueTask ApplyAsync(IMessageContext context)
    {
        await context.BroadcastToTopicAsync(TopicName, this, new DeliveryOptions{ContentType = IntrinsicSerializer.MimeType});
    }

    public byte[] Write()
    {
        return Array.Empty<byte>();
    }

    public static object Read(byte[] bytes)
    {
        // It's never really used
        return new Ping();
    }
}