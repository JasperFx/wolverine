using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime;
using Wolverine.Runtime.Serialization.Encryption;
using Wolverine.Util;

namespace Wolverine.ErrorHandling;

internal sealed class FaultPublisher : IFaultPublisher
{
    private readonly FaultPublishingPolicy _policy;
    private readonly IWolverineRuntime _runtime;
    private readonly ILogger<FaultPublisher> _logger;
    private readonly Counter<int> _publishFailedCounter;
    private readonly ConcurrentDictionary<Type, Func<object, ExceptionInfo, Envelope, object>> _factories = new();

    public FaultPublisher(
        FaultPublishingPolicy policy,
        IWolverineRuntime runtime,
        ILogger<FaultPublisher> logger,
        Meter meter)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        if (meter is null) throw new ArgumentNullException(nameof(meter));

        _publishFailedCounter = meter.CreateCounter<int>(
            MetricsConstants.FaultsPublishFailed,
            unit: MetricsConstants.Messages,
            description: "Number of auto-Fault<T> publishes that failed (logged and swallowed).");
    }

    public async ValueTask PublishIfEnabledAsync(
        IEnvelopeLifecycle lifecycle,
        Exception exception,
        FaultTrigger trigger,
        Activity? activity)
    {
        var original = lifecycle.Envelope;
        if (original?.Message is null) return;

        var messageType = original.Message.GetType();

        // Recursion guard: never publish a Fault<Fault<T>>. A failed Fault<T>
        // subscriber falls through to the standard failure pipeline instead.
        if (messageType.IsGenericType && messageType.GetGenericTypeDefinition() == typeof(Fault<>))
        {
            _logger.LogDebug(
                "Suppressed auto-publish of Fault<{MessageType}> — message is itself a Fault<T> (recursion guard); envelope {EnvelopeId}",
                messageType.FullName, original.Id);
            activity?.AddEvent(new ActivityEvent(WolverineTracing.FaultRecursionSuppressed));
            return;
        }

        // Silent no-op for value-type messages — Fault<T> requires `T : class`.
        if (messageType.IsValueType) return;

        var mode = _policy.Resolve(messageType);

        if (mode == FaultPublishingMode.None) return;
        if (trigger == FaultTrigger.Discarded && mode != FaultPublishingMode.DlqAndDiscard) return;

        try
        {
            var exceptionInfo = ExceptionInfo.From(exception);
            var factory = _factories.GetOrAdd(messageType, BuildFactory);
            var faultMessage = factory(original.Message, exceptionInfo, original);

            var options = new DeliveryOptions();
            options.Headers[FaultHeaders.AutoPublished] = "true";
            options.Headers[FaultHeaders.OriginalId] = original.Id.ToString();
            options.Headers[FaultHeaders.OriginalType] = messageType.ToMessageTypeName();

            var router = _runtime.RoutingFor(faultMessage.GetType());
            var outgoing = router.RouteForPublish(faultMessage, options);
            if (outgoing.Length == 0)
            {
                _logger.LogDebug(
                    "No routes configured for auto-published Fault<{MessageType}>; envelope {EnvelopeId} skipped",
                    messageType.FullName, original.Id);
                activity?.AddEvent(new ActivityEvent(WolverineTracing.FaultNoRoute));
                return;
            }

            await lifecycle.PublishAsync(faultMessage, options);

            activity?.AddEvent(new ActivityEvent(WolverineTracing.FaultPublished));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to publish auto Fault<{MessageType}> for envelope {EnvelopeId}",
                messageType.FullName, original.Id);
            _publishFailedCounter.Add(
                1,
                new KeyValuePair<string, object?>(MetricsConstants.MessageTypeKey, messageType.FullName ?? messageType.Name),
                new KeyValuePair<string, object?>(MetricsConstants.ExceptionType, ex.GetType().FullName ?? ex.GetType().Name));

            activity?.SetStatus(ActivityStatusCode.Error, ex.GetType().Name);
            activity?.AddEvent(new ActivityEvent(WolverineTracing.FaultPublishFailed));
        }
    }

    // Reflective construction is fine: this only fires on terminal failures, never on the hot path.
    private static Func<object, ExceptionInfo, Envelope, object> BuildFactory(Type messageType)
    {
        var faultType = typeof(Fault<>).MakeGenericType(messageType);
        return (msg, info, env) => Activator.CreateInstance(
            faultType,
            msg,
            info,
            env.Attempts,
            DateTimeOffset.UtcNow,
            env.CorrelationId,                                      // string?
            env.ConversationId,                                     // Guid
            env.TenantId,                                           // string?
            env.Source,                                             // string?
            FilterEncryptionHeaders(env.Headers))!;
    }

    // Strip wolverine.encryption.* keys when materializing Fault<T>.Headers.
    // Those headers are routing/AAD metadata for the original envelope; they
    // would mislead consumers inspecting the Fault payload (and leak the
    // active key-id) if copied verbatim into the Fault body.
    private static Dictionary<string, string?> FilterEncryptionHeaders(Dictionary<string, string?> source)
    {
        var copy = new Dictionary<string, string?>(source.Count);
        foreach (var kv in source)
        {
            if (kv.Key.StartsWith(EncryptionHeaders.HeaderPrefix, StringComparison.Ordinal))
                continue;
            copy[kv.Key] = kv.Value;
        }
        return copy;
    }
}
