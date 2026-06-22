using DotPulsar;
using DotPulsar.Abstractions;
using JasperFx.Core;
using JasperFx.Descriptors;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.Pulsar;

public class PulsarTransport : TransportBase<PulsarEndpoint>, IAsyncDisposable
{
    public const string ProtocolName = "pulsar";

    private readonly LightweightCache<Uri, PulsarEndpoint> _endpoints;

    public PulsarTransport() : base(ProtocolName, "Pulsar", ["pulsar"])
    {
        Builder = PulsarClient.Builder();

        _endpoints =
            new LightweightCache<Uri, PulsarEndpoint>(uri => new PulsarEndpoint(uri, this));
    }

    public PulsarEndpoint this[Uri uri] => _endpoints[uri];

    [IgnoreDescription]
    public IPulsarClientBuilder Builder { get; }

    internal IPulsarClient? Client { get; private set; }
    /// <summary>
    /// Transport-wide default dead letter topic applied to every Pulsar endpoint that does not set
    /// its own <see cref="PulsarEndpoint.DeadLetterTopic"/>. Resolution is per-endpoint-override-wins:
    /// an endpoint reads its effective value through <see cref="PulsarEndpoint.EffectiveDeadLetterTopic"/>
    /// (per-endpoint override, else this default). Mirrors the Kafka transport default + per-endpoint
    /// override shape. Set via <c>UsePulsar(...).DeadLetterQueueing(...)</c>.
    /// </summary>
    public DeadLetterTopic? DeadLetterTopic { get; internal set; }

    /// <summary>
    /// Transport-wide default retry letter topic, resolved per-endpoint-override-wins through
    /// <see cref="PulsarEndpoint.EffectiveRetryLetterTopic"/>. Set via
    /// <c>UsePulsar(...).RetryLetterQueueing(...)</c>.
    /// </summary>
    public RetryLetterTopic? RetryLetterTopic { get; internal set; }


    //private IEnumerable<DeadLetterTopic> enabledDeadLetterTopics()
    //{
    //    if (DeadLetterTopic.Mode != DeadLetterTopicMode.WolverineStorage)
    //    {
    //        yield return DeadLetterTopic;
    //    }

    //    foreach (var queue in endpoints())
    //    {
    //        if (queue.IsPersistent && queue.Role == EndpointRole.Application && queue.DeadLetterTopic != null &&
    //            queue.DeadLetterTopic.Mode != DeadLetterTopicMode.WolverineStorage)
    //        {
    //            yield return queue.DeadLetterTopic;
    //        }
    //    }
    //}

    //public IEnumerable<RetryLetterTopic> enabledRetryLetterTopics()
    //{
    //    if (RetryLetterTopic != null)
    //    {
    //        yield return RetryLetterTopic;
    //    }
    //    foreach (var queue in endpoints())
    //    {
    //        if (queue.IsPersistent && queue.Role == EndpointRole.Application && queue.RetryLetterTopic != null)
    //        {
    //            yield return queue.RetryLetterTopic;
    //        }
    //    }
    //}

    public ValueTask DisposeAsync()
    {
        if (Client != null)
        {
            return Client.DisposeAsync();
        }

        return ValueTask.CompletedTask;
    }

    protected override IEnumerable<PulsarEndpoint> endpoints()
    {
        return _endpoints;
    }

    protected override PulsarEndpoint findEndpointByUri(Uri uri)
    {
        return _endpoints[uri];
    }

    public override ValueTask InitializeAsync(IWolverineRuntime runtime)
    {
        Client = Builder.Build();
        configureRetryTopics(runtime);
        return ValueTask.CompletedTask;
    }

    // GH-3182: discover the MoveToPulsarRetryTopic error policy in the global failure rules and wire its
    // per-tier delays onto every Pulsar listener endpoint (provisioning the native retry-letter
    // producer/consumer + DLQ), plus startup validation warnings for non-Pulsar listeners (where the
    // policy degrades to an inline retry) and for unsupported subscription types (where Pulsar message
    // delaying does not work).
    private void configureRetryTopics(IWolverineRuntime runtime)
    {
        var continuation = runtime.Options.HandlerGraph.Failures
            .Select(rule => rule.InfiniteSource)
            .OfType<ErrorHandling.MoveToPulsarRetryTopicContinuation>()
            .FirstOrDefault();

        if (continuation == null)
        {
            return;
        }

        var logger = runtime.LoggerFactory.CreateLogger<PulsarTransport>();

        var hasNonPulsarListener = runtime.Options.Transports
            .Where(t => t is not PulsarTransport)
            .SelectMany(t => t.Endpoints())
            .Any(e => e.IsListener);
        if (hasNonPulsarListener)
        {
            logger.LogWarning(
                "MoveToPulsarRetryTopic is configured but non-Pulsar listeners are present. The Pulsar retry-letter topics only apply to messages received over Pulsar; failures on other transports will fall back to an inline retry.");
        }

        var delays = continuation.Delays.ToList();

        foreach (var endpoint in endpoints().Where(e => e.IsListener))
        {
            // Respect an explicit RetryLetterQueueing(...) / DeadLetterQueueing(...) on the endpoint;
            // otherwise provision the native pipeline from the policy's delays.
            endpoint.RetryLetterTopic ??= new RetryLetterTopic(delays);
            endpoint.DeadLetterTopic ??= DeadLetterTopic.DefaultNative;

            if (!RetryLetterTopic.SupportedSubscriptionTypes.Contains(endpoint.SubscriptionType))
            {
                logger.LogWarning(
                    "MoveToPulsarRetryTopic requires a Shared or KeyShared subscription, but listener {Topic} uses {SubscriptionType}; the tiered retry-letter delays will not apply and failures will fall back to an inline retry.",
                    endpoint.PulsarTopic(), endpoint.SubscriptionType);
            }
        }
    }

    public WolverineTransportHealthCheck BuildHealthCheck(IWolverineRuntime runtime)
    {
        return new PulsarHealthCheck(this);
    }

    public PulsarEndpoint EndpointFor(string topicPath)
    {
        var uri = PulsarEndpointUri.Topic(topicPath);
        return this[uri];
    }
}
