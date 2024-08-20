using Wolverine.Configuration;
using Wolverine.Logging;
using Wolverine.Runtime;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports.Sending;

namespace Wolverine.Transports.Local;

internal class BufferedLocalQueue : BufferedReceiver, ISendingAgent, IListenerCircuit
{
    private readonly IMessageTracker _messageTracker;

    public BufferedLocalQueue(Endpoint endpoint, IWolverineRuntime runtime) : base(endpoint, runtime, new HandlerPipeline((WolverineRuntime)runtime, (IExecutorFactory)runtime, endpoint))
    {
        _messageTracker = runtime.MessageTracking;
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

    void IListenerCircuit.EnqueueDirectly(IEnumerable<Envelope> envelopes)
    {
        foreach (var envelope in envelopes)
        {
            EnqueueDirectly(envelope);
        }
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