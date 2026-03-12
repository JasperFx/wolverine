using System.Diagnostics;
using JasperFx.Core;

namespace Wolverine.Runtime;

internal static class WolverineTracing
{
    // See https://opentelemetry.io/docs/reference/specification/trace/semantic_conventions/messaging/ for more information

    public const string MessageType = "messaging.message_type";

    // This needs to be the correlation id. Not necessarily the same thing as the message id
    public const string MessagingConversationId = "messaging.conversation_id"; // The Wolverine correlation Id
    public const string MessagingMessageId = "messaging.message_id";
    public const string MessagingSystem = "messaging.system"; // Use the destination Uri scheme
    public const string MessagingDestination = "messaging.destination"; // Use the destination Uri

    public const string MessageHandler = "message.handler";

    public const string
        MessagingDestinationKind =
            "messaging.destination_kind"; // Not sure this is going to be helpful. queue or topic. Maybe port if TCP basically.

    public const string MessagingTempDestination = "messaging.temp_destination"; // boolean if this is temporary
    public const string PayloadSizeBytes = "messaging.message_payload_size_bytes";

    #region sample_wolverine_open_telemetry_tracing_spans_and_activities

    /// <summary>
    /// ActivityEvent marking when an incoming envelope is discarded
    /// </summary>
    public const string EnvelopeDiscarded = "wolverine.envelope.discarded";

    /// <summary>
    /// ActivityEvent marking when an incoming envelope is being moved to the error queue
    /// </summary>
    public const string MovedToErrorQueue = "wolverine.error.queued";
    
    /// <summary>
    /// ActivityEvent marking when an incoming envelope does not have a known message
    /// handler and is being shunted to registered "NoHandler" actions
    /// </summary>
    public const string NoHandler = "wolverine.no.handler";
    
    /// <summary>
    /// ActivityEvent marking when a message failure is configured to pause the message listener
    /// where the message was handled. This is tied to error handling policies
    /// </summary>
    public const string PausedListener = "wolverine.paused.listener";
    
    /// <summary>
    /// Span that is emitted when a listener circuit breaker determines that there are too many
    /// failures and listening should be paused
    /// </summary>
    public const string CircuitBreakerTripped = "wolverine.circuit.breaker.triggered";
    
    /// <summary>
    /// Span emitted when a listening agent is started or restarted
    /// </summary>
    public const string StartingListener = "wolverine.starting.listener";
    
    /// <summary>
    /// Span emitted when a listening agent is stopping
    /// </summary>
    public const string StoppingListener = "wolverine.stopping.listener";
    
    /// <summary>
    /// Span emitted when a listening agent is being paused
    /// </summary>
    public const string PausingListener = "wolverine.pausing.listener";
    
    /// <summary>
    /// ActivityEvent marking that an incoming envelope is being requeued after a message
    /// processing failure
    /// </summary>
    public const string EnvelopeRequeued = "wolverine.envelope.requeued";
    
    /// <summary>
    /// ActivityEvent marking that an incoming envelope is being retried after a message
    /// processing failure
    /// </summary>
    public const string EnvelopeRetry = "wolverine.envelope.retried";
    
    /// <summary>
    /// ActivityEvent marking than an incoming envelope has been rescheduled for later
    /// execution after a failure
    /// </summary>
    public const string ScheduledRetry = "wolverine.envelope.rescheduled";
    
    /// <summary>
    /// Tag name trying to explain why a sender or listener was stopped or paused
    /// </summary>
    public const string StopReason = "wolverine.stop.reason";

    /// <summary>
    /// The Wolverine Uri that identifies what sending or listening endpoint the activity
    /// refers to
    /// </summary>
    public const string EndpointAddress = "wolverine.endpoint.address";
    
    /// <summary>
    /// A stop reason when back pressure policies call for a pause in processing in a single endpoint
    /// </summary>
    public const string TooBusy = "TooBusy";

    /// <summary>
    /// A span emitted when a sending agent for a specific endpoint is paused
    /// </summary>
    public const string SendingPaused = "wolverine.sending.pausing";
    
    /// <summary>
    /// A span emitted when a sending agent is resuming after having been paused
    /// </summary>
    public const string SendingResumed = "wolverine.sending.resumed";
    
    /// <summary>
    /// A stop reason when sending agents are paused after too many sender failures
    /// </summary>
    public const string TooManySenderFailures = "TooManySenderFailures";

    /// <summary>
    /// Span emitted when a streaming handler begins streaming responses
    /// </summary>
    public const string StreamingStarted = "wolverine.streaming.started";

    /// <summary>
    /// Span emitted when a streaming handler completes successfully
    /// </summary>
    public const string StreamingCompleted = "wolverine.streaming.completed";

    /// <summary>
    /// ActivityEvent marking when a streaming handler yields a message
    /// </summary>
    public const string StreamingMessageYielded = "wolverine.streaming.message.yielded";

    /// <summary>
    /// ActivityEvent marking when a streaming handler encounters an error
    /// </summary>
    public const string StreamingError = "wolverine.streaming.error";

    /// <summary>
    /// Tag name for the total count of messages streamed
    /// </summary>
    public const string StreamingMessageCount = "wolverine.streaming.message.count";

    /// <summary>
    /// Tag name for the duration of the streaming operation in milliseconds
    /// </summary>
    public const string StreamingDurationMs = "wolverine.streaming.duration.ms";

    /// <summary>
    /// Tag name for the type of message being streamed
    /// </summary>
    public const string StreamingMessageType = "wolverine.streaming.message.type";

    #endregion

    public static ActivitySource ActivitySource { get; } = new(
        "Wolverine",
        typeof(WolverineTracing).Assembly.GetName().Version!.ToString());

    public static Activity? StartSending(Envelope envelope)
    {
        return StartEnvelopeActivity("send", envelope, ActivityKind.Producer);
    }

    public static Activity? StartReceiving(Envelope envelope)
    {
        return StartEnvelopeActivity("receive", envelope, ActivityKind.Consumer);
    }

    public static Activity? StartExecuting(Envelope envelope)
    {
        return StartEnvelopeActivity(envelope.MessageType ?? "process", envelope);
    }

    public static Activity? StartEnvelopeActivity(string spanName, Envelope envelope,
        ActivityKind kind = ActivityKind.Internal)
    {
        var activity = envelope.ParentId.IsNotEmpty()
            ? ActivitySource.StartActivity(spanName, kind, envelope.ParentId)
            : ActivitySource.StartActivity(spanName, kind);

        if (activity == null)
        {
            return null;
        }

        envelope.WriteTags(activity);

        return activity;
    }

    internal static void MaybeSetTag<T>(this Activity activity, string tagName, T? value)
    {
        if (value != null)
        {
            activity.SetTag(tagName, value);
        }
    }

    /// <summary>
    /// Start a streaming activity for gRPC or other streaming operations
    /// </summary>
    public static Activity? StartStreaming<T>(string? correlationId = null)
    {
        var activity = ActivitySource.StartActivity(StreamingStarted, ActivityKind.Internal);
        if (activity == null) return null;

        activity.SetTag(StreamingMessageType, typeof(T).Name);
        if (correlationId.IsNotEmpty())
        {
            activity.SetTag(MessagingConversationId, correlationId);
        }

        return activity;
    }

    /// <summary>
    /// Record a streamed message event
    /// </summary>
    public static void RecordStreamedMessage<T>(this Activity? activity, T message)
    {
        activity?.AddEvent(new ActivityEvent(StreamingMessageYielded,
            tags: new ActivityTagsCollection
            {
                { StreamingMessageType, typeof(T).Name }
            }));
    }

    /// <summary>
    /// Complete a streaming activity with metrics
    /// </summary>
    public static void CompleteStreaming(this Activity? activity, int messageCount, TimeSpan duration)
    {
        if (activity == null) return;

        activity.SetTag(StreamingMessageCount, messageCount);
        activity.SetTag(StreamingDurationMs, duration.TotalMilliseconds);
        activity.AddEvent(new ActivityEvent(StreamingCompleted));
    }

    /// <summary>
    /// Record a streaming error
    /// </summary>
    public static void RecordStreamingError(this Activity? activity, Exception exception)
    {
        if (activity == null) return;

        activity.SetStatus(ActivityStatusCode.Error, exception.Message);
        activity.AddEvent(new ActivityEvent(StreamingError,
            tags: new ActivityTagsCollection
            {
                { "exception.type", exception.GetType().FullName },
                { "exception.message", exception.Message },
                { "exception.stacktrace", exception.StackTrace }
            }));
    }
}