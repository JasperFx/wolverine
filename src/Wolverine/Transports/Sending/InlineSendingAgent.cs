using JasperFx.Blocks;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Logging;
using Wolverine.Runtime;

namespace Wolverine.Transports.Sending;

public class InlineSendingAgent : ISendingAgent, IDisposable
{
    private readonly ILogger _logger;
    private readonly IMessageTracker _messageLogger;
    private readonly IRetryBlock<Envelope> _sending;
    private readonly DurabilitySettings _settings;
    private readonly IWolverineRuntime? _runtime;
    private readonly SendingFailurePolicies? _sendingFailurePolicies;

    public InlineSendingAgent(ILogger logger, ISender sender, Endpoint endpoint, IMessageTracker messageLogger,
        DurabilitySettings settings) : this(logger, sender, endpoint, messageLogger, settings, null, null)
    {
    }

    public InlineSendingAgent(ILogger logger, ISender sender, Endpoint endpoint, IMessageTracker messageLogger,
        DurabilitySettings settings, IWolverineRuntime? runtime, SendingFailurePolicies? sendingFailurePolicies)
    {
        _logger = logger;
        Sender = sender;
        _messageLogger = messageLogger;
        _settings = settings;
        Endpoint = endpoint;
        _runtime = runtime;
        _sendingFailurePolicies = sendingFailurePolicies;

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
    public bool SupportsNativeScheduledCancellation => Sender.SupportsNativeScheduledCancellation;

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
        catch (Exception ex)
        {
            if (!await tryHandleSendingFailureAsync(e, ex))
            {
                throw;
            }
        }
        finally
        {
            activity?.Stop();
        }
    }

    private async Task sendWithOutTracing(Envelope e, CancellationToken cancellationToken)
    {
        try
        {
            await Sender.SendAsync(e);
            _messageLogger.Sent(e);
        }
        catch (Exception ex)
        {
            if (!await tryHandleSendingFailureAsync(e, ex))
            {
                throw;
            }
        }
    }

    private async Task<bool> tryHandleSendingFailureAsync(Envelope envelope, Exception exception)
    {
        if (_sendingFailurePolicies == null || !_sendingFailurePolicies.HasAnyRules || _runtime == null)
        {
            return false;
        }

        envelope.SendAttempts++;

        var continuation = _sendingFailurePolicies.DetermineAction(exception, envelope);
        if (continuation == null)
        {
            return false;
        }

        try
        {
            var lifecycle = new SendingEnvelopeLifecycle(envelope, _runtime, this, null);
            await continuation.ExecuteAsync(lifecycle, _runtime, DateTimeOffset.UtcNow, null);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing sending failure policy for envelope {EnvelopeId}", envelope.Id);
            return false;
        }
    }

    private void setDefaults(Envelope envelope)
    {
        envelope.Status = EnvelopeStatus.Outgoing;
        envelope.OwnerId = _settings.AssignedNodeNumber;
        envelope.ReplyUri ??= ReplyUri;
    }
}
