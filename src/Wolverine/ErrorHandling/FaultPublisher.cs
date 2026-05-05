using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime;

namespace Wolverine.ErrorHandling;

internal sealed class FaultPublisher : IFaultPublisher
{
    private readonly FaultPublishingPolicy _policy;
    private readonly ILogger<FaultPublisher> _logger;
    private readonly Counter<long> _publishFailedCounter;
    private readonly ConcurrentDictionary<Type, Func<object, ExceptionInfo, Envelope, object>> _factories = new();

    public FaultPublisher(FaultPublishingPolicy policy, ILogger<FaultPublisher> logger, Meter meter)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        if (meter is null) throw new ArgumentNullException(nameof(meter));

        _publishFailedCounter = meter.CreateCounter<long>(
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
            new Dictionary<string, string?>(env.Headers))!;
    }
}
