using MQTTnet.Extensions.ManagedClient;
using Wolverine.Transports.Sending;

namespace Wolverine.MQTT.Internals;

/// <summary>
/// A standalone <see cref="ISender"/> bound to a specific <see cref="IManagedMqttClient"/>. Broker-per-tenant
/// (GH-3307) uses one of these per tenant (bound to the tenant's dedicated client) beneath a
/// <see cref="TenantedSender"/>, plus one bound to the default client as the fallback path.
///
/// It enqueues directly to the managed client (fire-and-forget) and deliberately does NOT implement
/// <see cref="ISenderRequiresCallback"/>: <see cref="TenantedSender"/> does not forward RegisterCallback to the
/// senders beneath it, so a callback-requiring sender there would silently drop every message (GH-2361).
/// </summary>
internal class MqttTopicSender : ISender
{
    private readonly MqttTopic _topic;
    private readonly IManagedMqttClient _client;

    public MqttTopicSender(MqttTopic topic, IManagedMqttClient client)
    {
        _topic = topic;
        _client = client;
    }

    public bool SupportsNativeScheduledSend => false;
    public Uri Destination => _topic.Uri;

    public async Task<bool> PingAsync()
    {
        try
        {
            await _client.PingAsync();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public ValueTask SendAsync(Envelope envelope)
    {
        var message = _topic.BuildMessage(envelope);
        return new ValueTask(_client.EnqueueAsync(message));
    }
}
