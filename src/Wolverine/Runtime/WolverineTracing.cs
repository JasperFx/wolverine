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

    public const string
        MessagingDestinationKind =
            "messaging.destination_kind"; // Not sure this is going to be helpful. queue or topic. Maybe port if TCP basically.

    public const string MessagingTempDestination = "messaging.temp_destination"; // boolean if this is temporary
    public const string PayloadSizeBytes = "messaging.message_payload_size_bytes";

    public const string EnvelopeDiscarded = "wolverine.envelope.discarded";
    public const string MovedToErrorQueue = "wolverine.error.queued";
    public const string NoHandler = "wolverine.no.handler";
    public const string PausedListener = "wolverine.paused.listener";
    public const string EnvelopeRequeued = "wolverine.envelope.requeued";
    public const string EnvelopeRetry = "wolverine.envelope.retried";
    public const string ScheduledRetry = "wolverine.envelope.rescheduled";

    internal static ActivitySource ActivitySource { get; } = new(
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
        return StartEnvelopeActivity("process", envelope);
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
}