using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Logging;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Runtime.Serialization;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports.Sending;

namespace Wolverine.Transports.Local;

internal class DurableLocalQueue : DurableReceiver, ISendingAgent
{
    private readonly IMessageLogger _messageLogger;
    private readonly IEnvelopePersistence _persistence;
    private readonly IMessageSerializer _serializer;
    private readonly AdvancedSettings _settings;

    public DurableLocalQueue(Endpoint endpoint, IWolverineRuntime runtime) : base(endpoint, runtime, runtime.Pipeline)
    {
        _settings = runtime.Advanced;
        _persistence = runtime.Persistence;
        _messageLogger = runtime.MessageLogger;
        _serializer = endpoint.DefaultSerializer ??
                      throw new ArgumentOutOfRangeException(nameof(endpoint),
                          "No default serializer for this Endpoint");
        Destination = endpoint.Uri;

        Endpoint = endpoint;
        ReplyUri = TransportConstants.RepliesUri;
    }

    public Uri Destination { get; }

    public Endpoint Endpoint { get; }

    public Uri? ReplyUri { get; set; }

    public bool Latched => false;

    public bool IsDurable => true;

    public ValueTask EnqueueOutgoingAsync(Envelope envelope)
    {
        _messageLogger.Sent(envelope);

        Enqueue(envelope);

        return ValueTask.CompletedTask;
    }

    public async ValueTask StoreAndForwardAsync(Envelope envelope)
    {
        _messageLogger.Sent(envelope);
        writeMessageData(envelope);

        // TODO -- have to watch this one
        envelope.Status = envelope.IsScheduledForLater(DateTimeOffset.Now)
            ? EnvelopeStatus.Scheduled
            : EnvelopeStatus.Incoming;

        envelope.OwnerId = envelope.Status == EnvelopeStatus.Incoming
            ? _settings.UniqueNodeId
            : TransportConstants.AnyNode;

        await _persistence.StoreIncomingAsync(envelope);

        if (envelope.Status == EnvelopeStatus.Incoming)
        {
            Enqueue(envelope);
        }
    }

    public bool SupportsNativeScheduledSend { get; } = true;


    private void writeMessageData(Envelope envelope)
    {
        if (envelope.Message is null)
        {
            throw new ArgumentOutOfRangeException(nameof(envelope), "Envelope.Message is null");
        }

        if (envelope.Data == null || envelope.Data.Length == 0)
        {
            _serializer.Write(envelope);
            envelope.ContentType = _serializer.ContentType;
        }
    }
}
