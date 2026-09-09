using System.Buffers;
using System.Diagnostics;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.MultiTenancy;
using MassTransit;
using System.Text.Json.Serialization;
using Wolverine.Attributes;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime.Serialization;
using Wolverine.Util;

namespace Wolverine;

[MessageIdentity("envelope")]
public partial class Envelope : IHasTenantId
{
    public static readonly string PingMessageType = "wolverine-ping";
    private byte[]? _data;

    // GH-4333. Non-null only when a payload arrived over the pooled path, i.e. was at least
    // PooledBodyThreshold bytes. _data and _pooledBody are never both meaningful: reading Data while
    // this is set materializes an INDEPENDENT array into _data and leaves the rental in place, so a
    // caller that stashed Data keeps working after the buffer goes back to the pool.
    private byte[]? _pooledBody;
    private int _pooledLength;
    private DateTimeOffset? _deliverBy;

    private TimeSpan? _deliverWithin;

    private object? _message;

    private TimeSpan? _scheduleDelay;
    private DateTimeOffset? _scheduledTime;

    /// <summary>
    /// Create an envelope for a batched message
    /// </summary>
    /// <param name="message"></param>
    /// <param name="items"></param>
    /// <returns></returns>
    public Envelope(object message, IEnumerable<Envelope> batch)
    {
        Message = message;
        Batch = batch.ToArray();
        foreach (var envelope in Batch)
        {
            envelope.InBatch = true;
        }
    }
    
    public Envelope()
    {
    }

    public Envelope(object message)
    {
        Message = message ?? throw new ArgumentNullException(nameof(message));
        MessageType = message?.GetType().ToMessageTypeName();
    }
    
    private Dictionary<string, string?>? _headers;

    /// <summary>
    ///     Optional metadata about this message
    /// </summary>
    public Dictionary<string, string?> Headers
    {
        get => _headers ??= new();
        internal set => _headers = value;
    }

    /// <summary>
    /// True if any headers have been recorded on this envelope. Unlike touching
    /// <see cref="Headers"/>, this never forces the lazy dictionary to allocate.
    /// </summary>
    public bool HasHeaders => _headers is { Count: > 0 };

    /// <summary>
    /// Try to read a header value by key without forcing dictionary allocation.
    /// Returns true if the header exists and has a non-null value.
    /// </summary>
    public bool TryGetHeader(string key, out string? value)
    {
        if (_headers != null && _headers.TryGetValue(key, out value))
        {
            return value != null;
        }

        value = null;
        return false;
    }

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

    /// <summary>
    ///     Set the DeliverBy property to have this message thrown away
    ///     if it cannot be sent before the allotted time. This value if set
    ///     is retained for testing purposes
    /// </summary>
    /// <value></value>
    [JsonIgnore]
    public TimeSpan? DeliverWithin
    {
        set
        {
            if (value == null)
            {
                throw new ArgumentNullException(nameof(value));
            }

            DeliverBy = DateTimeOffset.UtcNow.Add(value.Value);
            _deliverWithin = value;
        }
        get => _deliverWithin;
    }

    /// <summary>
    ///     Set the ScheduleTime to now plus the value of the supplied TimeSpan.
    ///     If set, this value is retained just for the sake of testing
    /// </summary>
    public TimeSpan? ScheduleDelay
    {
        set
        {
            _scheduleDelay = value;
            if (value != null)
            {
                ScheduledTime = DateTimeOffset.UtcNow.Add(value.Value);
            }
        }
        get => _scheduleDelay;
    }

    public async ValueTask<byte[]?> GetDataAsync()
    {
        if (_data != null)
        {
            return _data;
        }
        assertMessage();

        if(Serializer is IAsyncMessageSerializer asyncMessaeSerializer)
        {
            try
            {
                releasePooledBody();
                _data = await asyncMessaeSerializer.WriteAsync(this);
            }
            catch (Exception e)
            {
                throw new WolverineSerializationException(
                    $"Error trying to serialize message of type {Message!.GetType().FullNameInCode()} with serializer {Serializer}", e);
            }
        }

        return Data;
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

            if (_pooledBody != null)
            {
                // GH-4333. Materialize ONCE into an independent array and keep it. The rental stays
                // where it is: a caller who reads Data is allowed to stash the result, so the array they
                // get back must not be one the pool can hand to somebody else.
                _data = _pooledBody.AsSpan(0, _pooledLength).ToArray();
                return _data;
            }

            assertMessage();

            if (Serializer == null)
            {
                if (_message is ISerializable serializable)
                {
                    _data = serializable.Write();
                    return _data;
                }

                throw new WolverineSerializationException($"No data or writer is known for this envelope of message type {_message!.GetType().FullNameInCode()}");
            }

            try
            {
                _data = Serializer.Write(this);
            }
            catch (Exception e)
            {
                throw new WolverineSerializationException(
                    $"Error trying to serialize message of type {Message!.GetType().FullNameInCode()} with serializer {Serializer}", e);
            }

            return _data;
        }
        set
        {
            releasePooledBody();
            _data = value;
        }
    }

    /// <summary>
    ///     GH-4333. The serialized message data as a <see cref="ReadOnlyMemory{T}" />, without forcing the
    ///     copy that reading <see cref="Data" /> can. Prefer this on any hot path that only needs to READ
    ///     the payload -- writing it to a broker, hashing it, measuring it.
    /// </summary>
    /// <remarks>
    ///     The memory is only valid for the lifetime of this envelope. When the payload came in over the
    ///     pooled path the underlying array goes back to <see cref="ArrayPool{T}" /> when the envelope is
    ///     reset, and anything still holding this <c>ReadOnlyMemory</c> is then reading a buffer somebody
    ///     else owns. To keep the bytes, read <see cref="Data" /> -- it materializes an independent array
    ///     that outlives the pool.
    /// </remarks>
    public ReadOnlyMemory<byte> Body
    {
        get
        {
            if (_data != null)
            {
                return _data;
            }

            if (_pooledBody != null)
            {
                return _pooledBody.AsMemory(0, _pooledLength);
            }

            // No payload yet: fall through to Data, which serializes the message on demand exactly as it
            // does for any other reader, and hand back whatever that produced
            return Data ?? ReadOnlyMemory<byte>.Empty;
        }
    }

    /// <summary>
    ///     GH-4333. Payloads at or above this size are copied into a pooled buffer on the receive path
    ///     instead of a fresh array.
    /// </summary>
    /// <remarks>
    ///     85,000 bytes is the large-object-heap threshold, and it is the gate for a reason. Below it a
    ///     per-message <c>byte[]</c> is a cheap gen-0 bump -- the measured prize at 1 KB is ~26ns, which
    ///     does not pay for a rental, a length field and a lifetime to get wrong. At and above it the
    ///     allocation changes character: it lands on the LOH, is not compacted, and drives gen-2
    ///     collections. That is where pooling is worth its complexity, and nowhere else.
    /// </remarks>
    public const int PooledBodyThreshold = 85_000;

    /// <summary>
    ///     GH-4333. Take a copy of a broker's delivery buffer, using a pooled array when the payload is
    ///     large enough to be worth it. The copy itself is not optional -- a client's buffer is only valid
    ///     for the duration of its callback -- so this changes where the bytes land, not how many times
    ///     they are copied.
    /// </summary>
    public void CopyBodyFrom(ReadOnlySpan<byte> body)
    {
        releasePooledBody();

        if (body.Length < PooledBodyThreshold)
        {
            // Small payloads keep exactly the shape they have always had. No rental, no length field,
            // no lifetime: the gate exists so the overwhelming majority of messages never meet any of it.
            _data = body.ToArray();
            return;
        }

        _data = null;
        _pooledBody = ArrayPool<byte>.Shared.Rent(body.Length);
        _pooledLength = body.Length;
        body.CopyTo(_pooledBody);
    }

    /// <summary>
    ///     Hand the pooled buffer back. Deliberately only called from <see cref="Reset" /> and from the
    ///     <see cref="Data" /> setter -- both points where this envelope demonstrably no longer refers to
    ///     the buffer. Never returning is merely a missed optimization; returning too early hands a live
    ///     buffer to the next renter, so the bias is always towards not returning.
    /// </summary>
    private void releasePooledBody()
    {
        if (_pooledBody == null) return;

        var buffer = _pooledBody;
        _pooledBody = null;
        _pooledLength = 0;
        ArrayPool<byte>.Shared.Return(buffer);
    }

    private void assertMessage()
    {
        if (_message == null)
        {
            throw new WolverineSerializationException($"Cannot ensure data is present when there is no message. The Message Type Name is '{MessageType}'");
        }
    }

    // GH-4333: a pooled payload has a length even though _data is still null, and reading Data to find
    // it out would force the very copy the pooled path exists to avoid
    internal int? MessagePayloadSize => _data?.Length ?? (_pooledBody != null ? _pooledLength : null);

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
    public int Attempts { get; set; }

    /// <summary>
    ///     Number of times that Wolverine has tried to send this message.
    ///     This is tracked separately from handler Attempts and is used
    ///     by sending failure policies.
    /// </summary>
    public int SendAttempts { get; set; }

    /// <summary>
    ///     The <b>broker's</b> own count of how many times it has delivered this message, when the
    ///     transport reports one — RabbitMQ's <c>x-death</c> count, Amazon SQS's
    ///     <c>ApproximateReceiveCount</c>, Azure Service Bus's <c>DeliveryCount</c>. Null on a transport
    ///     that has no such concept, and on the first delivery of transports that only count redeliveries.
    /// </summary>
    /// <remarks>
    ///     GH-4012 item 4. This is the one delivery counter that <b>survives envelope reconstruction</b>:
    ///     every redelivery builds a brand new <c>RabbitMqEnvelope</c> / <c>AzureServiceBusEnvelope</c>, so
    ///     <see cref="Attempts" /> and <c>AckAttempts</c> both restart at zero and neither can bound a
    ///     redeliver → dedupe → re-ack loop. Only the broker remembers across those boundaries.
    /// </remarks>
    public int? BrokerDeliveryCount { get; set; }

    public DateTimeOffset SentAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Wall-clock UTC timestamp set inside <see cref="MarkReceived"/> when the envelope
    /// is handed off from a listener to the receiver pipeline. Stays <c>null</c> for
    /// envelopes that haven't been through a receiver yet (e.g. outbound). Read by the
    /// opt-in <c>wolverine.envelope.receive_dwell_ms</c> activity tag from
    /// <see cref="TrackingOptions.HandlerExecutionDiagnosticsEnabled"/>; not serialized.
    /// </summary>
    [JsonIgnore]
    public DateTimeOffset? ReceivedAt { get; set; }

    /// <summary>
    ///     The name of the service that sent this envelope
    /// </summary>
    public string? Source { get; set; }

    /// <summary>
    ///     Message type alias for the contents of this Envelope
    /// </summary>
    public string? MessageType { get; set; }

    /// <summary>
    /// Set the MessageType to Wolverine's message type name for
    /// T
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public void SetMessageType<T>()
    {
        SetMessageType(typeof(T));
    }

    /// <summary>
    /// Set the MessageType to Wolverine's message type name for
    /// this message type
    /// </summary>
    /// <param name="messageType"></param>
    public void SetMessageType(Type messageType)
    {
        MessageType = messageType.ToMessageTypeName();
    }

    /// <summary>
    ///     Location where any replies should be sent
    /// </summary>
    public Uri? ReplyUri { get; set; }

    /// <summary>
    ///     Mimetype of the serialized data
    /// </summary>
    public string? ContentType { get; set; }

    /// <summary>
    ///     Correlating identifier for the logical workflow or system action
    /// </summary>
    public string? CorrelationId { get; set; }

    /// <summary>
    ///     If this message is part of a stateful saga, this property identifies
    ///     the underlying saga state object
    /// </summary>
    public string? SagaId { get; set; }

    /// <summary>
    ///     Id of the immediate message or workflow that caused this envelope to be sent
    /// </summary>
    public Guid ConversationId { get; set; }

    /// <summary>
    ///     Location that this message should be sent
    /// </summary>
    public Uri? Destination { get; set; }

    /// <summary>
    ///     The open telemetry activity parent id. Wolverine uses this to correctly correlate connect
    ///     activity across services
    /// </summary>
    public string? ParentId { get; set; }

    /// <summary>
    ///     The open telemetry activity id of the immediately preceding failed attempt at processing this
    ///     envelope, if any. Set by error handling continuations that reschedule or retry an envelope
    ///     (<see cref="Wolverine.ErrorHandling.RetryInlineContinuation"/>, <see cref="Wolverine.ErrorHandling.ScheduledRetryContinuation"/>,
    ///     <see cref="Wolverine.ErrorHandling.RequeueContinuation"/>) so that the next attempt's activity can
    ///     be linked back to it instead of being reparented under it. A retry does not temporally contain the
    ///     attempt that preceded it, so OpenTelemetry's own guidance is to express that relationship with an
    ///     <see cref="System.Diagnostics.ActivityLink"/> rather than a parent/child span.
    /// </summary>
    public string? PreviousAttemptActivityId { get; set; }

    /// <summary>
    ///     User defined tenant identifier for multi-tenancy strategies. This is
    ///     part of metrics reporting and message correlation
    /// </summary>
    public string? TenantId { get; set; }

    /// <summary>
    ///     The authenticated user name for tracking and auditing purposes
    /// </summary>
    public string? UserName { get; set; }

    /// <summary>
    ///     Specifies the accepted content types for the requested reply
    /// </summary>
    internal static readonly string?[] DefaultAcceptedContentTypes = ["application/json"];

    public string?[] AcceptedContentTypes { get; set; } = DefaultAcceptedContentTypes;

    /// <summary>
    /// The delegate used to generate unique envelope IDs. Defaults to NewId.NextSequentialGuid.
    /// Set via <see cref="WolverineOptions.EnvelopeIdGeneration"/> at startup.
    /// </summary>
    internal static Func<Guid> IdGenerator = NewId.NextSequentialGuid;

    /// <summary>
    ///     Specific message id for this envelope
    /// </summary>
    public Guid Id { get; set; } = IdGenerator();

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
    /// Used internally to understand where an envelope is in persisted state
    /// </summary>
    public bool WasPersistedInOutbox { get; set; }

    /// <summary>
    /// Application defined message group identifier. Part of AMQP 1.0 spec as the "group-id" property. Session identifier
    /// for Azure Service Bus.  MessageGroupId for Amazon SQS FIFO Queue. This is the Group Id for Kafka consumers, if there is one
    /// </summary>
    public string? GroupId { get; set; }

    /// <summary>
    /// GH-4180. The application defined *logical* identity of this message -- "rebuild projection X
    /// for tonight's 03:00 run" -- as opposed to <see cref="Id"/>, which identifies one delivery.
    /// Round-trips on every transport under the "deduplication-id" wire header, and is additionally
    /// used as the native MessageDeduplicationId for Amazon SQS/SNS FIFO and GCP Pub/Sub.
    ///
    /// Set it explicitly with <c>DeliveryOptions.DeduplicationId</c>, or let Wolverine derive it from
    /// the message with <c>[DeduplicationIdentity]</c> or <c>opts.MessageDeduplication</c>. It is
    /// enforced by <c>[Deduplicated]</c> handlers when
    /// <c>opts.Durability.EnableMessageDeduplication</c> is on.
    /// </summary>
    public string? DeduplicationId { get; set; }

    /// <summary>
    /// Key partition for Kafka
    /// </summary>
    public string? PartitionKey { get; set; }

    /// <summary>
    ///     Schedule this envelope to be sent or executed
    ///     after a delay
    /// </summary>
    /// <param name="delay"></param>
    /// <returns></returns>
    public Envelope ScheduleDelayed(TimeSpan delay)
    {
        ScheduledTime = DateTimeOffset.UtcNow.Add(delay);
        return this;
    }
    
    /// <summary>
    /// Used to "smuggle" contextual information to some
    /// messaging transports
    /// </summary>
    [JsonIgnore]
    public object? RoutingInformation { get; set; }

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

        if (CorrelationId.IsNotEmpty())
        {
            text += $"/CorrelationId={CorrelationId}";
        }

        if (Message != null)
        {
            text += $" ({Message.GetType().FullNameInCode()})";
        }

        if (Source != null)
        {
            text += $" from {Source}";
        }

        if (Destination != null)
        {
            text += $" to {Destination}";
        }

        if (TenantId.IsNotEmpty())
        {
            text += $" for tenant {TenantId}";
        }

        if (Batch != null)
        {
            text += $" as a batch of {Batch.Length}";
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
        // ReSharper disable once NonReadonlyMemberInGetHashCode
        return Id.GetHashCode();
    }

    /// <summary>
    ///     Should the processing of this message be scheduled for a later time
    /// </summary>
    /// <param name="utcNow"></param>
    /// <returns></returns>
    public bool IsScheduledForLater(DateTimeOffset utcNow)
    {
        // Doesn't matter, if it's been scheduled and persisted, it has 
        // to be scheduled
        if (Status == EnvelopeStatus.Scheduled) return true;
        
        return ScheduledTime.HasValue && ScheduledTime.Value > utcNow;
    }

    /// <summary>
    ///     Has this envelope expired according to its DeliverBy value
    /// </summary>
    /// <returns></returns>
    public bool IsExpired()
    {
        return DeliverBy.HasValue && DeliverBy <= DateTimeOffset.UtcNow;
    }

    internal string GetMessageTypeName()
    {
        return (Message?.GetType().Name ?? MessageType)!;
    }
    
    /// <summary>
    /// For stream based transports (Kafka/RedPanda, this will reflect the message offset. This is strictly informational
    /// </summary>
    public long Offset { get; set; }

    /// <summary>
    /// For stream based transports (Kafka), this will reflect the partition the message was consumed from. This is strictly informational
    /// </summary>
    public int? PartitionId { get; set; }


    /// <summary>
    /// For some forms of modular monoliths, Wolverine needs to track what message store
    /// persisted this envelope for later tracking
    /// </summary>
    [JsonIgnore]
    internal IMessageStore? Store { get; set; }

    public static Envelope ForPersistedHandled(Envelope original, DateTimeOffset now, DurabilitySettings settings)
    {
        return new Envelope
        {
            Id = original.Id,
            Data = [],
            OwnerId = 0,
            Status = EnvelopeStatus.Handled,
            Destination = original.Destination,
            MessageType = original.MessageType,
            KeepUntil = now.Add(settings.KeepAfterMessageHandling)
        };
    }
    
    /// <summary>
    /// Marks the time stamp for how long this envelope should be retained as
    /// "Handled" in the inbox for idempotency protections
    /// </summary>
    public DateTimeOffset? KeepUntil { get; set; }
}