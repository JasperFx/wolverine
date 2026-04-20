using Wolverine.Configuration;
using Wolverine.Logging;
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

    async Task IListenerCircuit.EnqueueDirectlyAsync(IEnumerable<Envelope> envelopes)
    {
        // Recovery path: when the durability agent moves persisted incoming envelopes back
        // to this non-durable local queue, route them through IReceiver.ReceivedAsync with
        // a wrapper listener that marks the inbox row as Handled when processing completes.
        // Without this wrapper the row would sit in wolverine_incoming forever — see
        // https://github.com/JasperFx/wolverine/issues/1942.
        var listener = new LocalQueueRecoveryListener(Destination, _runtime);
        await ((IReceiver)this).ReceivedAsync(listener, envelopes.ToArray());
    }

    public Uri Destination { get; }
    public Uri? ReplyUri { get; set; } = TransportConstants.RepliesUri;

    public bool Latched => false;

    public bool IsDurable => false;

    public ValueTask EnqueueOutgoingAsync(Envelope envelope)
    {
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