using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using JasperFx.Blocks;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Logging;
using Wolverine.Runtime;
using Wolverine.Util;

namespace Wolverine.Transports.Sending;

public class InlineSendingAgent : ISendingAgent, IAsyncDisposable
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

    // GH-4955. The rejection the sending handler met for an envelope, keyed by reference because Envelope's
    // equality is by id. The handler swallows a rejection rather than throwing it, so NEITHER retry block
    // retries it (the asynchronous one would retry three times at 50/100/250 ms and drop the message, the
    // synchronous one four times), and EnqueueOutgoingAsync reads it back to throw it to the caller.
    private readonly ConditionalWeakTable<Envelope, SendRejectedException> _rejections = new();

    public ISender Sender { get; }

    public async ValueTask DisposeAsync()
    {
        (_sending as IDisposable)?.Dispose();

        await Sender.MaybeDisposeAsync().ConfigureAwait(false);
    }

    public Uri Destination => Sender.Destination;
    public Uri? ReplyUri { get; set; }
    public bool Latched => false;
    public bool IsDurable => false;
    public bool SupportsNativeScheduledSend => Sender.SupportsNativeScheduledSend;

    public DateTimeOffset LastMessageSentAt { get; private set; } = DateTimeOffset.UtcNow;

    public async ValueTask EnqueueOutgoingAsync(Envelope envelope)
    {
        setDefaults(envelope);

        await _sending.PostAsync(envelope);

        // GH-4955: a refusal by the destination is the caller's to know about. A retry cannot succeed, so the
        // message was not sent and will not be; SendAsync() throws rather than returning as if it had
        if (_rejections.TryGetValue(envelope, out var rejection))
        {
            _rejections.Remove(envelope);
            ExceptionDispatchInfo.Capture(rejection).Throw();
        }

        LastMessageSentAt = DateTimeOffset.UtcNow;
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
            tryReturnToPool(e);
        }
        catch (NotSupportedException)
        {
            // ignore it
        }
        catch (Exception ex)
        {
            if (!await tryHandleSendingFailureAsync(e, ex))
            {
                if (rememberRejection(e, ex))
                {
                    return;
                }

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
            tryReturnToPool(e);
        }
        catch (Exception ex)
        {
            if (!await tryHandleSendingFailureAsync(e, ex))
            {
                if (rememberRejection(e, ex))
                {
                    return;
                }

                throw;
            }
        }
    }

    // GH-4955: only a rejection is remembered and swallowed here. Every other failure keeps the retry block's
    // own handling, and a sending failure policy that took the exception has already decided what happens to
    // the envelope. Logged at Warning, once: the caller is about to get the exception itself
    private bool rememberRejection(Envelope envelope, Exception exception)
    {
        if (exception is not SendRejectedException rejected)
        {
            return false;
        }

        _logger.LogWarning(rejected,
            "The destination {Destination} refused message {EnvelopeId} ({MessageType}); it will not be retried and the send fails",
            Destination, envelope.Id, envelope.MessageType);

        _rejections.AddOrUpdate(envelope, rejected);
        return true;
    }

    // wolverine#2955: outgoing-envelope pool release. Only fires for envelopes that
    // were acquired from the pool at the MessageRoute.CreateForSending site, i.e.
    // the envelope's FromPool flag is set. Lives in the success branch so a retried
    // envelope (still owned by the retry block) is never returned out from under
    // the retry.
    private void tryReturnToPool(Envelope envelope)
    {
        if (!envelope.FromPool) return;
        if (_runtime is WolverineRuntime runtime)
        {
            runtime.ReleaseInternalEnvelope(envelope, true);
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
        envelope.OwnerId = _settings.OutgoingOwnerId;
        envelope.ReplyUri ??= ReplyUri;
    }
}
