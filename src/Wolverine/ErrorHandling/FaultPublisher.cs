using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Runtime.Serialization.Encryption;
using Wolverine.Util;

namespace Wolverine.ErrorHandling;

internal sealed class FaultPublisher : IFaultPublisher
{
    private readonly FaultPublishingPolicy _policy;
    private readonly IWolverineRuntime _runtime;
    private readonly ILogger<FaultPublisher> _logger;
    private readonly Counter<int> _publishFailedCounter;
    private readonly ConcurrentDictionary<Type, FaultShape> _shapes = new();

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
            MetricsConstants.FaultPublishFailures,
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

        var decision = _policy.Resolve(messageType);
        var routed = decision.Mode != FaultPublishingMode.None &&
                     (trigger != FaultTrigger.Discarded || decision.Mode == FaultPublishingMode.DlqAndDiscard);

        // GH-5553: the sender asked for this message's fault (FaultTo, ReturnFaultToSender, or a saga that handles
        // Fault<T>). It goes there when the message is dead-lettered, whether or not fault events are on
        var replyTo = trigger == FaultTrigger.MovedToErrorQueue ? replyAddressOf(original) : null;

        if (!routed && replyTo is null) return;

        try
        {
            // GH-5146: the Fault<T> constructor and the message type name are worked out once per message
            // type, and only once a fault is actually going to be published
            var shape = _shapes.GetOrAdd(messageType, FaultShape.For);

            var exceptionInfo = ExceptionInfo.From(
                exception,
                decision.IncludeExceptionMessage,
                decision.IncludeStackTrace);
            var faultMessage = shape.Create(original.Message, exceptionInfo, original);

            if (routed)
            {
                var options = faultOptions(original, shape);
                var router = _runtime.RoutingFor(faultMessage.GetType());
                var outgoing = router.RouteForPublish(faultMessage, options);
                if (outgoing is null || outgoing.Length == 0)
                {
                    _logger.LogDebug(
                        "No routes configured for auto-published Fault<{MessageType}>; envelope {EnvelopeId} skipped",
                        messageType.FullName, original.Id);
                    activity?.AddEvent(new ActivityEvent(
                        WolverineTracing.FaultNoRoute,
                        tags: new ActivityTagsCollection
                        {
                            [WolverineTracing.MessageType] = messageType.FullName
                        }));
                }
                else
                {
                    await lifecycle.PublishAsync(faultMessage, options);
                    activity?.AddEvent(new ActivityEvent(WolverineTracing.FaultPublished));

                    // GH-5553: both the routes and the reply address, but only once to any one address
                    if (replyTo is not null && outgoing.Any(x => sameDestination(x.Destination, replyTo)))
                    {
                        replyTo = null;
                    }
                }
            }

            if (replyTo is not null)
            {
                await lifecycle.EndpointFor(replyTo).SendAsync(faultMessage, faultOptions(original, shape));
                activity?.AddEvent(new ActivityEvent(WolverineTracing.FaultPublished));
            }
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

    // Two local queues reach the same handlers in this process, so a fault routed to one local queue is not sent
    // again to another
    private static bool sameDestination(Uri? routed, Uri replyTo)
    {
        if (routed is null) return false;
        if (routed.Equals(replyTo)) return true;

        return routed.Scheme == TransportConstants.Local && replyTo.Scheme == TransportConstants.Local;
    }

    private static DeliveryOptions faultOptions(Envelope original, FaultShape shape)
    {
        var options = new DeliveryOptions();
        options.Headers[FaultHeaders.AutoPublished] = "true";
        options.Headers[FaultHeaders.OriginalId] = original.Id.ToString();
        options.Headers[FaultHeaders.OriginalType] = shape.MessageTypeName;
        return options;
    }

    /// <summary>
    /// GH-5553. Where the sender asked for the fault of <paramref name="original" />: the address in its
    /// <see cref="FaultHeaders.ReplyTo" /> header, or its reply address for <see cref="FaultHeaders.ReplyToSender" />.
    /// Null when it asked for nothing, or for an address that cannot be used.
    /// </summary>
    private Uri? replyAddressOf(Envelope original)
    {
        if (!original.HasHeaders || !original.Headers.TryGetValue(FaultHeaders.ReplyTo, out var value) ||
            string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (value == FaultHeaders.ReplyToSender)
        {
            // A receiver fills in its own address when an envelope arrives with no reply address, and sending the
            // fault there would only send it back to this service
            if (original.ReplyUri is null || original.ReplyUri.Equals(original.Destination))
            {
                _logger.LogWarning(
                    "Envelope {EnvelopeId} ({MessageType}) asked for its fault to go back to the sender, but it carries no reply address of the sender's. No fault was sent back",
                    original.Id, original.MessageType);
                return null;
            }

            return original.ReplyUri;
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri)) return uri;

        _logger.LogWarning(
            "Envelope {EnvelopeId} ({MessageType}) asked for its fault to go to '{ReplyTo}', which is not an absolute address. No fault was sent there",
            original.Id, original.MessageType, value);
        return null;
    }

    /// <summary>
    ///     What <see cref="PublishIfEnabledAsync" /> needs to know about one message type, worked out once.
    ///     The Fault&lt;T&gt; constructor is bound once into a <see cref="ConstructorInvoker" /> rather than
    ///     resolved again by <see cref="Activator.CreateInstance(Type, object[])" /> on every failure.
    /// </summary>
    private sealed class FaultShape
    {
        private static readonly Type[] ConstructorArgumentTail =
        [
            typeof(ExceptionInfo), typeof(int), typeof(DateTimeOffset), typeof(string), typeof(Guid),
            typeof(string), typeof(string), typeof(IReadOnlyDictionary<string, string?>)
        ];

        private readonly ConstructorInvoker _constructor;

        private FaultShape(ConstructorInvoker constructor, string messageTypeName)
        {
            _constructor = constructor;
            MessageTypeName = messageTypeName;
        }

        public string MessageTypeName { get; }

        // Only called for a reference type that is not itself a Fault<T>; PublishIfEnabledAsync filters both
        public static FaultShape For(Type messageType)
        {
            return new FaultShape(ConstructorInvoker.Create(findConstructor(messageType)),
                messageType.ToMessageTypeName());
        }

        // Reflective construction is fine: this only fires on terminal failures, never on the hot path.
        private static ConstructorInfo findConstructor(Type messageType)
        {
            var faultType = typeof(Fault<>).MakeGenericType(messageType);
            Type[] argumentTypes = [messageType, ..ConstructorArgumentTail];
            return faultType.GetConstructor(argumentTypes)
                   ?? throw new InvalidOperationException($"Unable to find the primary constructor of {faultType}");
        }

        public object Create(object message, ExceptionInfo info, Envelope envelope)
        {
            var arguments = new object?[]
            {
                message,
                info,
                envelope.Attempts,
                DateTimeOffset.UtcNow,
                envelope.CorrelationId, // string?
                envelope.ConversationId, // Guid
                envelope.TenantId, // string?
                envelope.Source, // string?
                FilterEncryptionHeaders(envelope.Headers)
            };

            return _constructor.Invoke(arguments.AsSpan());
        }
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
