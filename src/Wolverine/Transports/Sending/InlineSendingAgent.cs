using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Logging;
using Wolverine.Runtime;
using Wolverine.Util.Dataflow;

namespace Wolverine.Transports.Sending;

public class InlineSendingAgent : ISendingAgent, IDisposable
{
    private readonly IMessageTracker _messageLogger;
    private readonly RetryBlock<Envelope> _sending;
    private readonly DurabilitySettings _settings;

    public InlineSendingAgent(ILogger logger, ISender sender, Endpoint endpoint, IMessageTracker messageLogger,
        DurabilitySettings settings)
    {
        Sender = sender;
        _messageLogger = messageLogger;
        _settings = settings;
        Endpoint = endpoint;

        if (endpoint.TelemetryEnabled)
        {
            _sending = new RetryBlock<Envelope>(sendWithTracing, logger, _settings.Cancellation);
        }
        else
        {
            _sending = new RetryBlock<Envelope>(sendWithOutTracing, logger, _settings.Cancellation);
        }
    }

    public ISender Sender { get; }

    private async Task sendWithTracing(Envelope e, CancellationToken cancellationToken)
    {
        using var activity = WolverineTracing.StartSending(e);
        try
        {
            await Sender.SendAsync(e);
            _messageLogger.Sent(e);
        }
        catch (NotSupportedException)
        {
            // ignore it
        }
        finally
        {
            activity?.Stop();
        }
    }

    private async Task sendWithOutTracing(Envelope e, CancellationToken cancellationToken)
    {
        await Sender.SendAsync(e);
        _messageLogger.Sent(e);
    }

    public void Dispose()
    {
        _sending.Dispose();
    }

    public Uri Destination => Sender.Destination;
    public Uri? ReplyUri { get; set; }
    public bool Latched => false;
    public bool IsDurable => false;
    public bool SupportsNativeScheduledSend => Sender.SupportsNativeScheduledSend;

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
        envelope.OwnerId = _settings.AssignedNodeNumber;
        envelope.ReplyUri ??= ReplyUri;
    }
}