using JasperFx.Core;
using MQTTnet;
using MQTTnet.Packets;
using Wolverine.Runtime.Serialization;

namespace Wolverine.MQTT.Internals;

public class MqttEnvelopeMapper : IMqttEnvelopeMapper
{
    private readonly MqttTopic _topic;

    public MqttEnvelopeMapper(MqttTopic topic)
    {
        _topic = topic;
    }

    public void MapEnvelopeToOutgoing(Envelope envelope, MqttApplicationMessage message)
    {
        message.ContentType = envelope.ContentType;
        message.ResponseTopic = _topic.Parent.ResponseTopic;
        message.Retain = _topic.Retain;
        message.QualityOfServiceLevel = _topic.QualityOfServiceLevel;
        message.PayloadSegment = envelope.Data;
        message.Topic = envelope.TopicName ?? _topic.TopicName; // Don't override so that user supplied topics can work!

        message.UserProperties =
        [
            new MqttUserProperty(EnvelopeConstants.AttemptsKey, envelope.Attempts.ToString()),
            new MqttUserProperty(EnvelopeConstants.MessageTypeKey, envelope.MessageType),
            new MqttUserProperty(EnvelopeConstants.IdKey, envelope.Id.ToString()),
            new MqttUserProperty(EnvelopeConstants.ConversationIdKey, envelope.ConversationId.ToString())
        ];

        foreach (var header in envelope.Headers)
        {
            message.UserProperties.Add(new MqttUserProperty(header.Key, header.Value));
        }

        if (envelope.CorrelationId.IsNotEmpty())
        {
            message.UserProperties.Add(new MqttUserProperty(EnvelopeConstants.CorrelationIdKey, envelope.CorrelationId));
        }

        if (envelope.ReplyUri != null)
        {
            message.UserProperties.Add(
                new MqttUserProperty(EnvelopeConstants.ReplyUriKey, envelope.ReplyUri.ToString()));
        }

        if (envelope.Source.IsNotEmpty())
        {
            message.UserProperties.Add(new MqttUserProperty(EnvelopeConstants.SourceKey, envelope.Source));
        }

        if (envelope.SagaId.IsNotEmpty())
        {
            message.UserProperties.Add(new MqttUserProperty(EnvelopeConstants.SagaIdKey, envelope.SagaId));
        }

        if (envelope.SentAt != default(DateTimeOffset))
        {
            message.UserProperties.Add(new MqttUserProperty(EnvelopeConstants.SentAtKey, envelope.SentAt.ToString()));
        }

        if (envelope.AcceptedContentTypes != null)
        {
            var accepted = new MqttUserProperty(EnvelopeConstants.AcceptedContentTypesKey, envelope.AcceptedContentTypes.Join(","));
            message.UserProperties.Add(accepted);
        }

        if (envelope.TenantId.IsNotEmpty())
        {
            message.UserProperties.Add(new MqttUserProperty(EnvelopeConstants.TenantIdKey, envelope.TenantId));
        }

        if (envelope.ParentId.IsNotEmpty())
        {
            message.UserProperties.Add(new MqttUserProperty(EnvelopeConstants.ParentIdKey, envelope.ParentId));
        }

        if (envelope.AckRequested)
        {
            message.UserProperties.Add(new MqttUserProperty(EnvelopeConstants.AckRequestedKey, true.ToString()));
        }

        if (envelope.ReplyRequested.IsNotEmpty())
        {
            message.UserProperties.Add(new MqttUserProperty(EnvelopeConstants.ReplyRequestedKey, envelope.ReplyRequested));
        }

        if (envelope.DeliverBy.HasValue)
        {
            message.UserProperties.Add(new MqttUserProperty(EnvelopeConstants.DeliverByKey, envelope.DeliverBy.ToString()));
        }

        if (envelope.IsResponse)
        {
            message.UserProperties.Add(new MqttUserProperty(EnvelopeConstants.IsResponseKey, true.ToString()));
        }
    }

    public void MapIncomingToEnvelope(Envelope envelope, MqttApplicationMessage incoming)
    {
        envelope.ContentType = incoming.ContentType;
        envelope.Data = incoming.PayloadSegment.ToArray();

        envelope.MessageType = _topic.MessageTypeName;
        envelope.TopicName = incoming.Topic;

        if (incoming.UserProperties?.Count > 0)
        {
            foreach (var property in incoming.UserProperties)
            {
                EnvelopeSerializer.ReadDataElement(envelope, property.Name, property.Value);
            }
        }
    }
}