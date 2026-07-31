using MQTTnet.Extensions.ManagedClient;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.MQTT.Internals;

/// <summary>
/// Publishes through the managed client's underlying <see cref="IManagedMqttClient.InternalClient"/>
/// directly, bypassing its internal enqueue-and-forget pending-message queue. For QoS >= AtLeastOnce
/// (the Wolverine default - see <see cref="MqttTopic.QualityOfServiceLevel"/>), the underlying
/// IMqttClient.PublishAsync call does not complete until the broker has actually acknowledged
/// (PUBACK/PUBCOMP) the message.
///
/// Paired with <see cref="BatchedSender"/> (which implements <see cref="ISenderRequiresCallback"/>),
/// this lets Wolverine's durable outbox/inbox (e.g. PersistMessagesWithSqlite combined with
/// UseDurableOutbox/UseDurableInbox) correctly defer ISenderCallback.MarkSuccessfulAsync - and
/// therefore deleting the persisted envelope - until delivery is genuinely confirmed, rather than
/// the fire-and-forget MqttTopicSender's "handed to the local client queue" signal.
/// </summary>
internal class MqttSenderProtocol : ISenderProtocol
{
    private readonly MqttTopic _topic;
    private readonly IManagedMqttClient _client;

    public MqttSenderProtocol(MqttTopic topic, IManagedMqttClient client)
    {
        _topic = topic;
        _client = client;
    }

    public async Task SendBatchAsync(ISenderCallback callback, OutgoingMessageBatch batch)
    {
        try
        {
            foreach (var envelope in batch.Messages)
            {
                var message = _topic.BuildMessage(envelope).ApplicationMessage;
                await _client.InternalClient.PublishAsync(message, CancellationToken.None);
            }

            await callback.MarkSuccessfulAsync(batch);
        }
        catch (Exception ex)
        {
            await callback.MarkProcessingFailureAsync(batch, ex);
        }
    }
}
