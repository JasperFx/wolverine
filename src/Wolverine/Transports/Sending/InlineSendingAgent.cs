using JasperFx.Blocks;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Logging;
using Wolverine.Runtime;

namespace Wolverine.Transports.Sending;

public class InlineSendingAgent : ISendingAgent, IDisposable
{
    private readonly IMessageTracker _messageLogger;
    private readonly IRetryBlock<Envelope> _sending;
    private readonly DurabilitySettings _settings;

    public InlineSendingAgent(ILogger logger, ISender sender, Endpoint endpoint, IMessageTracker messageLogger,
        DurabilitySettings settings)
    {
        Sender = sender;
        _messageLogger = messageLogger;
        _settings = settings;
        Endpoint = endpoint;

        if (settings.UseSyncRetryBlock)
        {
            _sending = new RetryBlockSync<Envelope>(RetryHandlerResolver(endpoint), logger, _settings.Cancellation);
        }
        else
        {
            _sending = new RetryBlock<Envelope>(RetryHandlerResolver(endpoint), logger, _settings.Cancellation);
        }
    }

    public ISender Sender { get; }

    public void Dispose()
    {
        (_sending as IDisposable)?.Dispose();
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

    private Func<Envelope, CancellationToken, Task> RetryHandlerResolver(Endpoint endpoint)
    {
        return endpoint.TelemetryEnabled ? sendWithTracing : sendWithOutTracing;
    }

    private async Task sendWithTracing(Envelope e, CancellationToken cancellationToken)
    {
        using var activity = WolverineTracing.StartSending(e);
        try
        {
            //TODO: What about cancellationToken??
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

    private void setDefaults(Envelope envelope)
    {
        envelope.Status = EnvelopeStatus.Outgoing;
        envelope.OwnerId = _settings.AssignedNodeNumber;
        envelope.ReplyUri ??= ReplyUri;
    }
}