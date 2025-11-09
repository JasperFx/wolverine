using System.Diagnostics;
using System.Runtime.InteropServices;
using JasperFx.Core;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;
using Wolverine.Transports.Local;
using Wolverine.Transports.Sending;
using Wolverine.Util;

namespace Wolverine;

public enum EnvelopeStatus
{
    Outgoing,
    Scheduled,
    Incoming,
    Handled
}

// Why is this a partial you ask?
// The elements in this file are all things that only matter
// inside the Wolverine runtime so we can keep it out of the WireProtocol
public partial class Envelope
{
    private bool _enqueued;

    private List<KeyValuePair<string, object?>>? _metricHeaders;
    private Stopwatch? _timer;

    internal Envelope(object message, ISendingAgent agent)
    {
        Message = message;
        Sender = agent;
        Serializer = message is ISerializable ? IntrinsicSerializer.Instance : agent.Endpoint.DefaultSerializer;
        ContentType = Serializer!.ContentType;
        Destination = agent.Destination;
        ReplyUri = agent.ReplyUri?.MaybeCorrectScheme(agent.Destination.Scheme);
    }

    internal Envelope(object message, IMessageSerializer writer)
    {
        Message = message;
        Serializer = writer ?? throw new ArgumentNullException(nameof(writer));
        ContentType = writer.ContentType;
    }

    public IMessageSerializer? Serializer { get; set; }

    /// <summary>
    ///     Used by IMessageContext.Invoke<T> to denote the response type
    /// </summary>
    internal Type? ResponseType { get; set; }

    /// <summary>
    ///     Also used by IMessageContext.Invoke<T> to catch the response
    /// </summary>
    internal object? Response { get; set; }
    
    /// <summary>
    /// Used internally only, tells Wolverine's cascading
    /// message logic to *not* publish the designated response
    /// message as a cascading message. Originally added for the
    /// Http transport request/reply
    /// </summary>
    public bool DoNotCascadeResponse { get; set; }

    /// <summary>
    ///     Status according to the message persistence
    /// </summary>
    public EnvelopeStatus Status { get; set; }

    /// <summary>
    ///     Node owner of this message. 0 denotes that no node owns this message
    /// </summary>
    public int OwnerId { get; set; }
    
    internal bool InBatch { get; set; }
    
    internal ISendingAgent? Sender { get; set; }

    public IListener? Listener { get; internal set; }
    public bool IsResponse { get; set; }
    public Exception? Failure { get; set; }
    internal Envelope[]? Batch { get; set; }

    internal void StartTiming()
    {
        _timer = new Stopwatch();
        _timer.Start();
    }

    internal long StopTiming()
    {
        if (_timer == null)
        {
            return 0;
        }

        _timer.Stop();
        return _timer.ElapsedMilliseconds;
    }

    /// <summary>
    /// How long did the current execution take?
    /// </summary>
    internal long ExecutionTime => _timer.ElapsedMilliseconds;

    /// <summary>
    /// </summary>
    /// <returns></returns>
    internal TagList ToMetricsHeaders()
    {
        var tagList = new TagList(CollectionsMarshal.AsSpan(_metricHeaders)) { { MetricsConstants.MessageTypeKey, MessageType } };

        if (Destination != null)
        {
            tagList.Add(MetricsConstants.MessageDestinationKey, Destination.ToString());
        }
        if (TenantId != null)
        {
            tagList.Add(MetricsConstants.TenantIdKey, TenantId);
        }

        return tagList;
    }

    /// <summary>
    ///     Add an additional tag for information written to metrics. This is purposely
    ///     kept separate from open telemetry activity tracking
    /// </summary>
    /// <param name="tagName"></param>
    /// <param name="value"></param>
    public void SetMetricsTag(string tagName, object value)
    {
        _metricHeaders ??= [];
        _metricHeaders.Add(new KeyValuePair<string, object?>(tagName, value));
    }

    internal void MarkReceived(IListener listener, DateTimeOffset now, DurabilitySettings settings)
    {
        Listener = listener;

        // If this is a stream with multiple consumers, use the consumer-specific address
        if (listener is ISupportMultipleConsumers multiConsumerListener)
        {
            Destination = multiConsumerListener.ConsumerAddress;
        }
        else
        {
            Destination = listener.Address;
        }

        if (IsScheduledForLater(now))
        {
            Status = EnvelopeStatus.Scheduled;
            OwnerId = TransportConstants.AnyNode;
        }
        else
        {
            Status = EnvelopeStatus.Incoming;
            OwnerId = settings.AssignedNodeNumber;
        }
    }

    /// <summary>
    ///     Create a new Envelope that is a response to the current
    ///     Envelope
    /// </summary>
    /// <param name="message"></param>
    /// <returns></returns>
    internal Envelope CreateForResponse(object message)
    {
        var child = new Envelope
        {
            Message = message,
            CorrelationId = Id.ToString(),
            ConversationId = Id,
            SagaId = SagaId,
            TenantId = TenantId
        };
        child.CorrelationId = CorrelationId;
        child.ConversationId = Id;

        if (message.GetType().ToMessageTypeName() == ReplyRequested)
        {
            child.Destination = ReplyUri;
            child.AcceptedContentTypes = AcceptedContentTypes;
        }

        if (message is ISerializable)
        {
            child.Serializer = IntrinsicSerializer.Instance;
            child.ContentType = IntrinsicSerializer.MimeType;
        }

        return child;
    }

    internal ValueTask StoreAndForwardAsync()
    {
        if (_enqueued)
        {
            throw new InvalidOperationException("This envelope has already been enqueued");
        }

        if (Sender == null)
        {
            throw new InvalidOperationException("This envelope has not been routed");
        }

        _enqueued = true;

        return Sender.StoreAndForwardAsync(this);
    }

    internal void PrepareForIncomingPersistence(DateTimeOffset now, DurabilitySettings settings)
    {
        Status = IsScheduledForLater(now)
            ? EnvelopeStatus.Scheduled
            : EnvelopeStatus.Incoming;

        OwnerId = Status == EnvelopeStatus.Incoming
            ? settings.AssignedNodeNumber
            : TransportConstants.AnyNode;
    }

    internal async ValueTask QuickSendAsync()
    {
        if (_enqueued)
        {
            throw new InvalidOperationException("This envelope has already been enqueued");
        }

        if (Sender == null)
        {
            throw new InvalidOperationException("This envelope has not been routed");
        }

        _enqueued = true;

        if (Sender.Latched)
        {
            // If the sender is latched, it indicates that the endpoint is currently unavailable 
            // (e.g., due to a network disconnection or a failure in the transport).
            // In such cases, we should *not* attempt to send the message immediately.
            //
            // Instead, if the sender is a SendingAgent, we explicitly mark the envelope as failed 
            // so that it will be retried later when the connection is re-established. 
            //
            // This conditional block ensures that latched agents skip enqueuing the message, 
            // but still track the failure to maintain durability and retry logic.
            if (Sender is SendingAgent sendingAgent)
            {
                await sendingAgent.MarkProcessingFailureAsync(this, null);
            }

            return;
        }

        if (Sender.Endpoint?.TelemetryEnabled ?? false)
        {
            using var activity = WolverineTracing.StartSending(this);
            try
            {
                await Sender.EnqueueOutgoingAsync(this);
            }
            finally
            {
                activity?.Stop();
            }
        }
        else
        {
            await Sender.EnqueueOutgoingAsync(this);
        }
    }

    /// <summary>
    ///     Is this envelope for a "ping" message used by Wolverine to evaluate
    ///     whether a sending endpoint can be restarted
    /// </summary>
    /// <returns></returns>
    public bool IsPing()
    {
        return MessageType == PingMessageType;
    }

    public static Envelope ForPing(Uri destination)
    {
        return new Envelope
        {
            MessageType = PingMessageType,
            Data = [1, 2, 3, 4],
            ContentType = "wolverine/ping",
            Destination = destination,
            
            // According to both AWS SQS & Azure Service Bus docs, it does no
            // harm to send a session identifier to a non-FIFO queue, and it's 
            // most certainly needed for FIFO queues
            GroupId = Guid.NewGuid().ToString()
        };
    }

    internal void WriteTags(Activity activity)
    {
        activity.MaybeSetTag(WolverineTracing.MessagingSystem, Destination?.Scheme); // This needs to vary
        activity.MaybeSetTag(WolverineTracing.MessagingDestination, Destination);
        activity.SetTag(WolverineTracing.MessagingMessageId, Id);
        activity.SetTag(WolverineTracing.MessagingConversationId, CorrelationId);
        activity.SetTag(WolverineTracing.MessageType, MessageType); // Wolverine specific
        activity.MaybeSetTag(WolverineTracing.PayloadSizeBytes, MessagePayloadSize);
        activity.MaybeSetTag(MetricsConstants.TenantIdKey, TenantId);
        activity.MaybeSetTag(WolverineTracing.MessagingConversationId, ConversationId);
    }

    internal ValueTask PersistAsync(IEnvelopeTransaction transaction)
    {
        if (Sender is { IsDurable: true })
        {
            if (Sender.Latched)
            {
                OwnerId = TransportConstants.AnyNode;
            }

            return new ValueTask(transaction.PersistAsync(this));
        }

        return ValueTask.CompletedTask;
    }

    internal bool IsFromLocalDurableQueue()
    {
        return Sender is DurableLocalQueue;
    }

    internal void MaybeCorrectReplyUri()
    {
        if (ReplyUri != null && Destination != null)
        {
            ReplyUri = ReplyUri.MaybeCorrectScheme(Destination.Scheme);
        }
    }

    internal DeliveryOptions ToDeliveryOptions()
    {
        return new DeliveryOptions
        {
            AckRequested = AckRequested,
            DeduplicationId = DeduplicationId,
            DeliverBy = DeliverBy,
            Headers = Headers,
            IsResponse = IsResponse,
            PartitionKey = PartitionKey,
            TenantId = TenantId,
            ScheduledTime = ScheduledTime,
            SagaId = SagaId
        };
    }
}