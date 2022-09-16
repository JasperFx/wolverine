using System;
using System.Threading.Tasks;
using Wolverine.Configuration;
using Wolverine.Logging;
using Wolverine.Runtime;

namespace Wolverine.Transports.Sending;

internal class InlineSendingAgent : ISendingAgent
{
    private readonly IMessageLogger _logger;
    private readonly ISender _sender;
    private readonly AdvancedSettings _settings;

    public InlineSendingAgent(ISender sender, Endpoint endpoint, IMessageLogger logger, AdvancedSettings settings)
    {
        _sender = sender;
        _logger = logger;
        _settings = settings;
        Endpoint = endpoint;
    }

    public Uri Destination => _sender.Destination;
    public Uri? ReplyUri { get; set; }
    public bool Latched { get; } = false;
    public bool IsDurable { get; } = false;
    public bool SupportsNativeScheduledSend => _sender.SupportsNativeScheduledSend;

    public async ValueTask EnqueueOutgoingAsync(Envelope envelope)
    {
        setDefaults(envelope);

        using var activity = WolverineTracing.StartSending(envelope);

        // TODO -- need to harden this for ephemeral failures
        await _sender.SendAsync(envelope);
        _logger.Sent(envelope);

        activity?.Stop();
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
