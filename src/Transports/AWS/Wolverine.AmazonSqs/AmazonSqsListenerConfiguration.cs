using System.Buffers.Text;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Amazon.SQS.Model;
using JasperFx.Core;
using Newtonsoft.Json;
using Wolverine.AmazonSqs.Internal;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Runtime.Interop.MassTransit;
using Wolverine.Runtime.Serialization;
using Wolverine.Util;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace Wolverine.AmazonSqs;

public class AmazonSqsListenerConfiguration : ListenerConfiguration<AmazonSqsListenerConfiguration, AmazonSqsQueue>
{
    internal AmazonSqsListenerConfiguration(AmazonSqsQueue endpoint) : base(endpoint)
    {
    }

    /// <summary>
    ///     Completely disable all SQS dead letter queueing for just this queue
    /// </summary>
    /// <returns></returns>
    public AmazonSqsListenerConfiguration DisableDeadLetterQueueing()
    {
        add(e => e.DeadLetterQueueName = null);
        return this;
    }

    /// <summary>
    ///     Customize the dead letter queueing for just this queue
    /// </summary>
    /// <param name="deadLetterQueue"></param>
    /// <param name="configure">Optionally configure properties of the dead letter queue itself</param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public AmazonSqsListenerConfiguration ConfigureDeadLetterQueue(string deadLetterQueue,
        Action<AmazonSqsQueue>? configure = null)
    {
        if (deadLetterQueue == null)
        {
            throw new ArgumentNullException(nameof(deadLetterQueue));
        }

        add(e =>
        {
            e.DeadLetterQueueName = AmazonSqsTransport.SanitizeSqsName(deadLetterQueue);
            if (configure != null)
            {
                e.ConfigureDeadLetterQueue(configure);
            }
        });

        return this;
    }

    /// <summary>
    ///     Add circuit breaker exception handling to this listener
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public AmazonSqsListenerConfiguration CircuitBreaker(Action<CircuitBreakerOptions>? configure = null)
    {
        add(e =>
        {
            e.CircuitBreakerOptions = new CircuitBreakerOptions();
            configure?.Invoke(e.CircuitBreakerOptions);
        });

        return this;
    }

    /// <summary>
    ///     Configure how the queue should be created within SQS
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public AmazonSqsListenerConfiguration ConfigureQueueCreation(Action<CreateQueueRequest> configure)
    {
        add(e => configure(e.Configuration));
        return this;
    }

    /// <summary>
    /// Configure this listener to receive raw JSON of an expected message type from
    /// an external system
    /// </summary>
    /// <param name="messageType"></param>
    /// <param name="configure"></param>
    /// <returns></returns>
    public AmazonSqsListenerConfiguration ReceiveRawJsonMessage(
        Type messageType,
        Action<JsonSerializerOptions>? configure = null)
    {
        add(e =>
        {
            JsonSerializerOptions serializerOptions = new JsonSerializerOptions();

            configure?.Invoke(serializerOptions);

            e.Mapper = new RawJsonSqsEnvelopeMapper(messageType, serializerOptions);
        });

        return this;
    }
    
    /// <summary>
    ///     Configure this listener to receive a message from an SNS topic subscription
    /// </summary>
    /// <param name="internalMessageMapper">The mapper for message forwarded from the SNS topic</param>
    /// <returns></returns>
    public AmazonSqsListenerConfiguration ReceiveSnsTopicMessage(ISqsEnvelopeMapper? internalMessageMapper = null)
    {
        add(e =>
        {
            internalMessageMapper ??= new DefaultSqsEnvelopeMapper();
            e.Mapper = new SnsTopicEnvelopeMapper(internalMessageMapper);
        });

        return this;
    } 

    /// <summary>
    /// Utilize custom envelope mapping for SQS interoperability with external non-Wolverine systems
    /// </summary>
    /// <param name="mapper"></param>
    /// <returns></returns>
    public AmazonSqsListenerConfiguration InteropWith(ISqsEnvelopeMapper mapper)
    {
        add(e => e.Mapper = mapper);
        return this;
    }

    /// <summary>
    /// Use an NServiceBus compatible enveloper mapper to interact with NServiceBus systems on the other end
    /// </summary>
    /// <returns></returns>
    /// <param name="replyQueueName">The name of an SQS queue that should be used for replies back from NServiceBus</param>
    public AmazonSqsListenerConfiguration UseNServiceBusInterop(string? replyQueueName = null)
    {
        add(e =>
        {
            e.DefaultSerializer = new NewtonsoftSerializer(new JsonSerializerSettings());
            e.Mapper = new NServiceBusEnvelopeMapper(replyQueueName, e);
        });

        return this;
    }

    public AmazonSqsListenerConfiguration UseMassTransitInterop(Action<IMassTransitInterop>? configure = null)
    {
        add(e => e.Mapper = new MassTransitMapper(Endpoint as IMassTransitInteropEndpoint));
        return this;
    }
}

internal class NServiceBusEnvelopeMapper : ISqsEnvelopeMapper
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

        var sqs = _serializer.ReadFromData<SqsEnvelope>(
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
    
    public string BuildMessageBody(Envelope envelope)
    {
        var data = Convert.ToBase64String(_serializer.WriteMessage(envelope.Message));
        var sqs = new SqsEnvelope(data, new())
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

internal record SqsEnvelope(string Body, Dictionary<string, string> Headers);

public static class DateTimeOffsetHelper
{
    /// <summary>
    /// Converts the <see cref="DateTimeOffset" /> to a <see cref="string" /> suitable for transport over the wire.
    /// </summary>
    public static string ToWireFormattedString(DateTimeOffset dateTime)
    {
        return dateTime.ToUniversalTime().ToString(format, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Converts a wire formatted <see cref="string" /> from <see cref="ToWireFormattedString" /> to a UTC
    /// <see cref="DateTimeOffset" />.
    /// </summary>
    public static DateTimeOffset ToDateTimeOffset(string wireFormattedString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(wireFormattedString);

        if (wireFormattedString.Length != format.Length)
        {
            throw new FormatException(errorMessage);
        }

        var year = 0;
        var month = 0;
        var day = 0;
        var hour = 0;
        var minute = 0;
        var second = 0;
        var microSecond = 0;

        for (var i = 0; i < format.Length; i++)
        {
            var digit = wireFormattedString[i];

            switch (format[i])
            {
                case 'y':
                    if (digit is < '0' or > '9')
                    {
                        throw new FormatException(errorMessage);
                    }

                    year = (year * 10) + (digit - '0');
                    break;

                case 'M':
                    if (digit is < '0' or > '9')
                    {
                        throw new FormatException(errorMessage);
                    }

                    month = (month * 10) + (digit - '0');
                    break;

                case 'd':
                    if (digit is < '0' or > '9')
                    {
                        throw new FormatException(errorMessage);
                    }

                    day = (day * 10) + (digit - '0');
                    break;

                case 'H':
                    if (digit is < '0' or > '9')
                    {
                        throw new FormatException(errorMessage);
                    }

                    hour = (hour * 10) + (digit - '0');
                    break;

                case 'm':
                    if (digit is < '0' or > '9')
                    {
                        throw new FormatException(errorMessage);
                    }

                    minute = (minute * 10) + (digit - '0');
                    break;

                case 's':
                    if (digit is < '0' or > '9')
                    {
                        throw new FormatException(errorMessage);
                    }

                    second = (second * 10) + (digit - '0');
                    break;

                case 'f':
                    if (digit is < '0' or > '9')
                    {
                        throw new FormatException(errorMessage);
                    }

                    microSecond = (microSecond * 10) + (digit - '0');
                    break;

                default:
                    break;
            }
        }

        var timestamp = new DateTimeOffset(year, month, day, hour, minute, second, TimeSpan.Zero);
        timestamp = timestamp.AddMicroseconds(microSecond);
        return timestamp;
    }

    const string format = "yyyy-MM-dd HH:mm:ss:ffffff Z";
    const string errorMessage = "String was not recognized as a valid DateTime.";
}