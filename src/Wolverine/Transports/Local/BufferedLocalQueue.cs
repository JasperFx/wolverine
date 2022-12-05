using System;
using System.Threading.Tasks;
using Wolverine.Configuration;
using Wolverine.Logging;
using Wolverine.Runtime;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports.Sending;

namespace Wolverine.Transports.Local;

internal class BufferedLocalQueue : BufferedReceiver, ISendingAgent
{
    private readonly IMessageLogger _messageLogger;

    public BufferedLocalQueue(Endpoint endpoint, IWolverineRuntime runtime) : base(endpoint, runtime, runtime.Pipeline)
    {
        _messageLogger = runtime.MessageLogger;
        Destination = endpoint.Uri;
        Endpoint = endpoint;
    }

    public Endpoint Endpoint { get; }

    public Uri Destination { get; }
    public Uri? ReplyUri { get; set; } = TransportConstants.RepliesUri;

    public bool Latched => false;

    public bool IsDurable => false;

    public ValueTask EnqueueOutgoingAsync(Envelope envelope)
    {
        _messageLogger.Sent(envelope);
        envelope.ReplyUri = envelope.ReplyUri ?? ReplyUri;

        if (envelope.IsScheduledForLater(DateTimeOffset.Now))
        {
            ScheduleExecution(envelope);
        }
        else
        {
            Enqueue(envelope);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask StoreAndForwardAsync(Envelope envelope)
    {
        return EnqueueOutgoingAsync(envelope);
    }

    public bool SupportsNativeScheduledSend { get; } = true;
}