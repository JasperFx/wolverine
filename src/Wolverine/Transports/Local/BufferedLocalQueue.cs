using Wolverine.Configuration;
using Wolverine.Logging;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports.Sending;

namespace Wolverine.Transports.Local;

internal class BufferedLocalQueue : BufferedReceiver, ISendingAgent, IListenerCircuit
{
    private readonly IMessageTracker _messageTracker;
    private readonly IWolverineRuntime _runtime;

    public BufferedLocalQueue(Endpoint endpoint, IWolverineRuntime runtime) : base(endpoint, runtime, new HandlerPipeline((WolverineRuntime)runtime, (IExecutorFactory)runtime, endpoint))
    {
        _messageTracker = runtime.MessageTracking;
        _runtime = runtime;
        Destination = endpoint.Uri;
        Endpoint = endpoint;
    }

    public ListeningStatus Status => ListeningStatus.Accepting;
    public Endpoint Endpoint { get; }

    // Edge case, but this actually happened to someone
    ValueTask IListenerCircuit.PauseAsync(TimeSpan pauseTime)
    {
        return ValueTask.CompletedTask;
    }

    ValueTask IListenerCircuit.StartAsync()
    {
        return ValueTask.CompletedTask;
    }

    Task IListenerCircuit.EnqueueDirectlyAsync(IEnumerable<Envelope> envelopes)
    {
        // Recovery path: when the durability agent moves persisted incoming envelopes back
        // to this non-durable local queue (either DLQ replay per GH-1942 or scheduled
        // message firing), attach a LocalQueueRecoveryListener so that the inbox row gets
        // marked Handled *after* the pipeline successfully completes. Without this, the
        // default BufferedReceiver.CompleteAsync is a no-op and the row sits in
        // wolverine_incoming forever.
        //
        // Note: we deliberately do NOT go through IReceiver.ReceivedAsync here — that
        // path fires _completeBlock eagerly at receipt time, which would mark scheduled
        // messages Handled before their handler has a chance to run.
        var listener = new LocalQueueRecoveryListener(Destination, _runtime);
        foreach (var envelope in envelopes)
        {
            envelope.Listener = listener;
            EnqueueDirectly(envelope);
        }

        return Task.CompletedTask;
    }

    public Uri Destination { get; }
    public Uri? ReplyUri { get; set; } = TransportConstants.RepliesUri;

    public bool Latched => false;

    public bool IsDurable => false;

    public ValueTask EnqueueOutgoingAsync(Envelope envelope)
    {
        // AlwaysMakeScheduledMessagesDurable: BufferedLocalQueue's "native" scheduling
        // is the in-process IScheduledJobProcessor — non-persistent, lost on restart.
        // The policy redirects scheduled envelopes to the durable inbox so they're
        // recovered by the scheduled-job poller after a crash. Recovery-path enqueues
        // (IListenerCircuit.EnqueueDirectlyAsync, line ~48) bypass this — those envelopes
        // came FROM the message store and must not be re-stored.
        if (envelope.IsScheduledForLater(DateTimeOffset.UtcNow)
            && _runtime.Options.Durability.AlwaysMakeScheduledMessagesDurable
            && _runtime.Storage is not NullMessageStore)
        {
            _messageTracker.Sent(envelope);
            envelope.ReplyUri ??= ReplyUri;
            // RescheduleExistingEnvelopeForRetryAsync is the misnamed-but-correct API for
            // both new scheduled envelopes and retry rescheduling — it sets Status=Scheduled
            // + OwnerId=AnyNode, then upserts via StoreIncomingAsync. Mirrors the call in
            // MessageContext.flushScheduledMessagesAsync. ScheduleExecutionAsync (UPDATE-only)
            // would silently no-op for envelopes that aren't already in the database.
            return new ValueTask(_runtime.Storage.Inbox.RescheduleExistingEnvelopeForRetryAsync(envelope));
        }

        EnqueueDirectly(envelope);

        return ValueTask.CompletedTask;
    }

    public ValueTask StoreAndForwardAsync(Envelope envelope)
    {
        return EnqueueOutgoingAsync(envelope);
    }

    public bool SupportsNativeScheduledSend => true;

    public DateTimeOffset LastMessageSentAt => DateTimeOffset.UtcNow;

    internal void EnqueueDirectly(Envelope envelope)
    {
        _messageTracker.Sent(envelope);
        envelope.ReplyUri = envelope.ReplyUri ?? ReplyUri;

        if (envelope.IsScheduledForLater(DateTimeOffset.Now))
        {
            ScheduleExecution(envelope);
        }
        else
        {
            Enqueue(envelope);
        }
    }
}