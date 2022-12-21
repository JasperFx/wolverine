using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Logging;
using Wolverine.Runtime;
using Wolverine.Util.Dataflow;

namespace Wolverine.Transports.Sending;

internal class InlineSendingAgent : ISendingAgent, IDisposable
{
    private readonly ISender _sender;
    private readonly RetryBlock<Envelope> _sending;
    private readonly AdvancedSettings _settings;

    public InlineSendingAgent(ILogger logger, ISender sender, Endpoint endpoint, IMessageLogger messageLogger,
        AdvancedSettings settings)
    {
        _sender = sender;
        _settings = settings;
        Endpoint = endpoint;

        _sending = new RetryBlock<Envelope>(async (e, _) =>
        {
            using var activity = WolverineTracing.StartSending(e);
            try
            {
                await _sender.SendAsync(e);
                messageLogger.Sent(e);
            }
            finally
            {
                activity?.Stop();
            }
        }, logger, _settings.Cancellation);
    }

    public void Dispose()
    {
        _sending.Dispose();
    }

    public Uri Destination => _sender.Destination;
    public Uri? ReplyUri { get; set; }
    public bool Latched => false;
    public bool IsDurable => false;
    public bool SupportsNativeScheduledSend => _sender.SupportsNativeScheduledSend;

    public ValueTask EnqueueOutgoingAsync(Envelope envelope)
    {
        setDefaults(envelope);

        return new ValueTask(_sending.PostAsync(envelope));
    }

    public ValueTask StoreAndForwardAsync(Envelope envelope)
    {
        return EnqueueOutgoingAsync(envelope);
    }

    public Endpoint Endpoint { get; }

    private void setDefaults(Envelope envelope)
    {
        envelope.Status = EnvelopeStatus.Outgoing;
        envelope.OwnerId = _settings.UniqueNodeId;
        envelope.ReplyUri ??= ReplyUri;
    }
}