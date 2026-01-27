using System.Globalization;
using System.Text;
using Amazon.SimpleNotificationService.Model;
using JasperFx.Core;
using Newtonsoft.Json;
using Wolverine.Runtime.Serialization;
using Wolverine.Util;
using Endpoint = Wolverine.Configuration.Endpoint;

namespace Wolverine.AmazonSns.Internal;

internal class NServiceBusEnvelopeMapper : ISnsEnvelopeMapper
{
    private readonly string _replyName;
    private readonly Endpoint _endpoint;

    private NewtonsoftSerializer _serializer = new NewtonsoftSerializer(new JsonSerializerSettings());

    public NServiceBusEnvelopeMapper(string replyName, Endpoint endpoint)
    {
        _replyName = replyName;
        _endpoint = endpoint;
    }

    public IEnumerable<KeyValuePair<string, MessageAttributeValue>> ToAttributes(Envelope envelope)
    {
        yield return new ("NServiceBus.ConversationId",
            new MessageAttributeValue{StringValue = envelope.ConversationId.ToString()});

        yield return new("NServiceBus.TimeSent", new MessageAttributeValue{StringValue = envelope.SentAt.ToUniversalTime().ToString("O")});

        yield return new("NServiceBus.CorrelationId", new MessageAttributeValue{StringValue = envelope.CorrelationId});

        if (_replyName.IsNotEmpty())
        {
            yield return new("NServiceBus.ReplyToAddress", new MessageAttributeValue { StringValue = _replyName });
        }
    }

    public void ReadEnvelopeData(Envelope envelope, string messageBody, IDictionary<string, MessageAttributeValue> attributes)
    {
        envelope.Serializer = _endpoint.DefaultSerializer;

        var sqs = _serializer.ReadFromData<SnsEnvelope>(
            Encoding.UTF8.GetBytes(messageBody));

        envelope.Data = Convert.FromBase64String(sqs.Body);
        

        if (sqs.Headers.TryGetValue("NServiceBus.MessageId", out var raw))
        {
            if (Guid.TryParse(raw, out var guid))
            {
                envelope.Id = guid;
            }
        }

        if (sqs.Headers.TryGetValue("NServiceBus.ConversationId", out var conversationId))
        {
            if (Guid.TryParse(conversationId, out var guid))
            {
                envelope.ConversationId = guid;
            }
        }

        if (sqs.Headers.TryGetValue("NServiceBus.CorrelationId", out var correlationId))
        {
            envelope.CorrelationId = correlationId;
        }

        if (sqs.Headers.TryGetValue("NServiceBus.ReplyToAddress", out var replyQueue))
        {
            envelope.ReplyUri = new Uri($"sqs://queue/{replyQueue}");
        }

        if (sqs.Headers.TryGetValue("NServiceBus.ContentType", out var contentType))
        {
            envelope.ContentType = contentType;
        }

        if (sqs.Headers.TryGetValue("NServiceBus.TimeSent", out var rawTime))
        {
            if (DateTimeOffset.TryParse(rawTime, new DateTimeFormatInfo{FullDateTimePattern = "yyyy-MM-dd HH:mm:ss:ffffff Z"}, DateTimeStyles.AssumeUniversal, out var result))
            {
                envelope.SentAt = result;
            }
        }

        if (sqs.Headers.TryGetValue("NServiceBus.EnclosedMessageTypes", out var messageTypeName))
        {
            Type messageType = Type.GetType(messageTypeName);
            if (messageType != null)
            {
                envelope.MessageType = messageType.ToMessageTypeName();
            }
            else
            {
                envelope.MessageType = messageTypeName;
            }
        }
    }
    
    internal record SnsEnvelope(string Body, Dictionary<string, string> Headers);

    
    public string BuildMessageBody(Envelope envelope)
    {
        var data = Convert.ToBase64String(_serializer.WriteMessage(envelope.Message));
        var sqs = new SnsEnvelope(data, new())
        {
            Headers =
            {
                ["NServiceBus.MessageId"] = envelope.Id.ToString(),
                ["NServiceBus.ConversationId"] = envelope.ConversationId.ToString(),
                ["NServiceBus.CorrelationId"] = envelope.CorrelationId,
                ["NServiceBus.ReplyToAddress"] = _replyName,
                ["NServiceBus.ContentType"] = "application/json",
                ["NServiceBus.TimeSent"] = envelope.SentAt.ToString("yyyy-MM-dd HH:mm:ss:ffffff Z"),
                ["NServiceBus.EnclosedMessageTypes"] = envelope.Message.GetType().ToMessageTypeName()
            }
        };

        return Encoding.UTF8.GetString(_serializer.WriteMessage(sqs));
    }
}