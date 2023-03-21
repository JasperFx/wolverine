using System;
using System.Collections.Generic;
using Wolverine.Attributes;
using Wolverine.Util;

namespace Wolverine;

[MessageIdentity("envelope")]
public partial class Envelope
{
    public static readonly string PingMessageType = "wolverine-ping";
    private byte[]? _data;
    private DateTimeOffset? _deliverBy;

    private object? _message;
    private DateTimeOffset? _scheduledTime;

    public Envelope()
    {
    }

    public Envelope(object message)
    {
        Message = message ?? throw new ArgumentNullException(nameof(message));
    }

    /// <summary>
    ///     Optional metadata about this message
    /// </summary>
    public Dictionary<string, string?> Headers { get; internal set; } = new();

    #region sample_envelope_deliver_by_property

    /// <summary>
    ///     Instruct Wolverine to throw away this message if it is not successfully sent and processed
    ///     by the time specified
    /// </summary>
    public DateTimeOffset? DeliverBy
    {
        get => _deliverBy;
        set => _deliverBy = value?.ToUniversalTime();
    }

    #endregion

    /// <summary>
    ///     Is an acknowledgement requested
    /// </summary>
    public bool AckRequested { get; internal set; }

    /// <summary>
    ///     Used by scheduled jobs or transports with a native scheduled send functionality to have this message processed by
    ///     the receiving application at or after the designated time
    /// </summary>
    public DateTimeOffset? ScheduledTime
    {
        get => _scheduledTime;
        set => _scheduledTime = value?.ToUniversalTime();
    }

    private TimeSpan? _deliverWithin;

    /// <summary>
    /// Set the DeliverBy property to have this message thrown away
    /// if it cannot be sent before the allotted time. This value if set
    /// is retained for testing purposes
    /// </summary>
    /// <value></value>
    public TimeSpan? DeliverWithin
    {
        set
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            DeliverBy = DateTimeOffset.Now.Add(value.Value);
            _deliverWithin = value;
        }
        get => _deliverWithin;
    }

    private TimeSpan? _scheduleDelay;
    
    /// <summary>
    /// Set the ScheduleTime to now plus the value of the supplied TimeSpan.
    /// If set, this value is retained just for the sake of testing
    /// </summary>
    public TimeSpan? ScheduleDelay
    {
        set
        {
            _scheduleDelay = value;
            if (value != null)
            {
                ScheduledTime = DateTimeOffset.Now.Add(value.Value);
            }
        }
        get => _scheduleDelay;
    }

    /// <summary>
    ///     The raw, serialized message data
    /// </summary>
    public byte[]? Data
    {
        get
        {
            if (_data != null)
            {
                return _data;
            }

            if (_message == null)
            {
                throw new InvalidOperationException("Cannot ensure data is present when there is no message");
            }

            if (Serializer == null)
            {
                throw new InvalidOperationException("No data or writer is known for this envelope");
            }

            // TODO -- this is messy!
            _data = Serializer.Write(this);

            return _data;
        }
        set => _data = value;
    }

    internal int? MessagePayloadSize => _data?.Length;

    /// <summary>
    ///     The actual message to be sent or being received
    /// </summary>
    public object? Message
    {
        get => _message;
        set
        {
            MessageType = value?.GetType().ToMessageTypeName();
            _message = value;
        }
    }

    /// <summary>
    ///     Number of times that Wolverine has tried to process this message. Will
    ///     reflect the current attempt number
    /// </summary>
    public int Attempts { get; internal set; }


    public DateTimeOffset SentAt { get; internal set; } = DateTimeOffset.UtcNow;

    /// <summary>
    ///     The name of the service that sent this envelope
    /// </summary>
    public string? Source { get; internal set; }


    /// <summary>
    ///     Message type alias for the contents of this Envelope
    /// </summary>
    public string? MessageType { get; set; }

    /// <summary>
    ///     Location where any replies should be sent
    /// </summary>
    public Uri? ReplyUri { get; internal set; }

    /// <summary>
    ///     Mimetype of the serialized data
    /// </summary>
    public string? ContentType { get; set; }

    /// <summary>
    ///     Correlating identifier for the logical workflow or system action
    /// </summary>
    public string? CorrelationId { get; internal set; }

    /// <summary>
    ///     If this message is part of a stateful saga, this property identifies
    ///     the underlying saga state object
    /// </summary>
    public string? SagaId { get; internal set; }

    /// <summary>
    ///     Id of the immediate message or workflow that caused this envelope to be sent
    /// </summary>
    public Guid ConversationId { get; internal set; }

    /// <summary>
    ///     Location that this message should be sent
    /// </summary>
    public Uri? Destination { get; set; }

    /// <summary>
    ///     The open telemetry activity parent id. Wolverine uses this to correctly correlate connect
    ///     activity across services
    /// </summary>
    public string? ParentId { get; internal set; }
    
    /// <summary>
    /// User defined tenant identifier for multi-tenancy strategies. This is
    /// part of metrics reporting and message correlation
    /// </summary>
    public string? TenantId { get; set; }

    /// <summary>
    ///     Specifies the accepted content types for the requested reply
    /// </summary>
    public string?[] AcceptedContentTypes { get; set; } = { "application/json" };

    /// <summary>
    ///     Specific message id for this envelope
    /// </summary>
    public Guid Id { get; set; } = CombGuidIdGeneration.NewGuid();

    /// <summary>
    ///     If specified, the message type alias for the reply message that is requested for this message
    /// </summary>
    public string? ReplyRequested { get; internal set; }

    /// <summary>
    ///     Designates the topic name for outgoing messages to topic-based publish/subscribe
    ///     routing. This property is only used for routing
    /// </summary>
    public string? TopicName { get; set; }

    /// <summary>
    ///     Purely informational in testing scenarios to record the endpoint
    ///     this envelope was published to
    /// </summary>
    public string? EndpointName { get; set; }

    /// <summary>
    ///     Schedule this envelope to be sent or executed
    ///     after a delay
    /// </summary>
    /// <param name="delay"></param>
    /// <returns></returns>
    public Envelope ScheduleDelayed(TimeSpan delay)
    {
        ScheduledTime = DateTimeOffset.Now.Add(delay);
        return this;
    }

    /// <summary>
    ///     Schedule this envelope to be sent or executed
    ///     at a certain time
    /// </summary>
    /// <param name="time"></param>
    /// <returns></returns>
    public Envelope ScheduleAt(DateTimeOffset time)
    {
        ScheduledTime = time;
        return this;
    }


    public override string ToString()
    {
        var text = $"Envelope #{Id}";
        if (Message != null)
        {
            text += $" ({Message.GetType().Name})";
        }

        if (Source != null)
        {
            text += $" from {Source}";
        }

        if (Destination != null)
        {
            text += $" to {Destination}";
        }


        return text;
    }


    protected bool Equals(Envelope other)
    {
        return Id.Equals(other.Id);
    }

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(null, obj))
        {
            return false;
        }

        if (ReferenceEquals(this, obj))
        {
            return true;
        }

        if (obj is Envelope envelope)
        {
            return Equals(envelope);
        }

        return false;
    }

    public override int GetHashCode()
    {
        return Id.GetHashCode();
    }

    /// <summary>
    ///     Should the processing of this message be scheduled for a later time
    /// </summary>
    /// <param name="utcNow"></param>
    /// <returns></returns>
    public bool IsScheduledForLater(DateTimeOffset utcNow)
    {
        return ScheduledTime.HasValue && ScheduledTime.Value > utcNow;
    }

    /// <summary>
    ///     Has this envelope expired according to its DeliverBy value
    /// </summary>
    /// <returns></returns>
    public bool IsExpired()
    {
        return DeliverBy.HasValue && DeliverBy <= DateTimeOffset.Now;
    }


    internal string GetMessageTypeName()
    {
        return (Message?.GetType().Name ?? MessageType)!;
    }
}