using DotPulsar;
using DotPulsar.Abstractions;
using JasperFx.Core;
using JasperFx.Descriptors;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Pulsar.Internal;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.Pulsar;

public class PulsarTransport : TransportBase<PulsarEndpoint>, IAsyncDisposable
{
    public const string ProtocolName = "pulsar";

    private readonly LightweightCache<Uri, PulsarEndpoint> _endpoints;

    public PulsarTransport() : this(ProtocolName)
    {
    }

    /// <summary>
    /// Constructor used when connecting to more than one Pulsar broker from a single application. The
    /// <paramref name="protocol"/> doubles as the additional broker's URI scheme so its endpoints do not
    /// collide with the default <c>pulsar://</c> broker. Reached through
    /// <see cref="TransportCollection.GetOrCreate{T}"/> when a <see cref="BrokerName"/> is supplied via
    /// <see cref="PulsarTransportExtensions.AddNamedPulsarBroker"/>.
    /// </summary>
    public PulsarTransport(string protocol) : base(protocol, "Pulsar", [protocol])
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
    /// Broker-per-tenant registrations (GH-3308). Each tenant owns a child <see cref="PulsarTransport"/>
    /// pointed at its own dedicated Pulsar cluster (service URL + auth via <see cref="IPulsarClientBuilder"/>);
    /// outbound is routed by <see cref="Envelope.TenantId"/> and inbound listeners stamp the tenant id.
    ///
    /// IMPORTANT: this Wolverine "tenant" is orthogonal to Pulsar's own native tenant segment in the
    /// <c>persistent://{tenant}/{namespace}/{topic}</c> topic hierarchy. The per-tenant differentiator here is a
    /// distinct <em>cluster connection</em>, never the native tenant segment (which is unchanged and shared).
    /// </summary>
    [IgnoreDescription]
    internal LightweightCache<string, PulsarTenant> Tenants { get; } = new();

    /// <summary>
    /// Controls how outbound messages whose <see cref="Envelope.TenantId"/> is null or does not match a
    /// registered tenant are handled when broker-per-tenant multi-tenancy is in effect. Defaults to
    /// <see cref="Wolverine.Transports.Sending.TenantedIdBehavior.FallbackToDefault"/>.
    /// </summary>
    public TenantedIdBehavior TenantedIdBehavior { get; set; } = TenantedIdBehavior.FallbackToDefault;
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

    public async ValueTask DisposeAsync()
    {
        if (Client != null)
        {
            await Client.DisposeAsync();
        }

        foreach (var tenant in Tenants)
        {
            await tenant.Transport.DisposeAsync();
        }
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

        // Broker-per-tenant (GH-3308): Pulsar connects here (not in ConnectAsync). Copy the parent defaults
        // onto each tenant's child transport and build its independent IPulsarClient against the tenant's own
        // cluster. The DotPulsar builder isn't cloneable, so each tenant supplies its own fully-specified
        // Builder (service URL + auth) via AddTenant.
        foreach (var tenant in Tenants)
        {
            tenant.Compile(this);
            tenant.Transport.Client = tenant.Transport.Builder.Build();
        }

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
        // Build the Wolverine endpoint URI with this transport's Protocol as the scheme. For the default broker
        // that is "pulsar://"; for a named broker (AddNamedPulsarBroker) it is the broker name, so the named
        // broker's endpoints route through ForScheme and never collide with the default broker's endpoints.
        var uri = PulsarEndpointUri.Topic(topicPath, Protocol);
        return this[uri];
    }

    /// <summary>
    /// Build the outbound sender for a Pulsar endpoint (GH-3308). When broker-per-tenant tenants are registered
    /// and the endpoint is tenant-aware, this wraps the default sender in a <see cref="TenantedSender"/> that
    /// dispatches on <see cref="Envelope.TenantId"/> and registers one <see cref="PulsarSender"/> per tenant
    /// cluster. Modeled on <c>RabbitMqTransport.BuildSender</c>.
    /// </summary>
    internal ISender BuildSender(PulsarEndpoint endpoint, IWolverineRuntime runtime)
    {
        var defaultSender = new PulsarSender(runtime, endpoint, this, runtime.Cancellation);

        if (Tenants.Any() && endpoint.TenancyBehavior == TenancyBehavior.TenantAware)
        {
            // PulsarSender is fire-and-forget (it does NOT implement ISenderRequiresCallback), so it is safe
            // under TenantedSender — no GH-2361 silent-drop concern.
            var tenantedSender = new TenantedSender(endpoint.Uri, TenantedIdBehavior, defaultSender);
            foreach (var tenant in Tenants)
            {
                var sender = new PulsarSender(runtime, endpoint, tenant.Transport, runtime.Cancellation);
                tenantedSender.RegisterSender(tenant.TenantId, sender);
            }

            return tenantedSender;
        }

        return defaultSender;
    }

    /// <summary>
    /// Build the inbound listener for a Pulsar endpoint (GH-3308). When broker-per-tenant tenants are registered
    /// and the endpoint is tenant-aware, this builds a <see cref="CompoundListener"/> that runs the default
    /// listener plus one per-tenant listener; each tenant listener wraps its receiver in a
    /// <see cref="ReceiverWithRules"/> carrying a <see cref="TenantIdRule"/> so inbound envelopes are stamped
    /// with the tenant id they were consumed under. The hot-tail (<see cref="PulsarReaderListener"/>) branch is
    /// preserved with the same per-tenant treatment. Modeled on <c>RabbitMqTransport.BuildListenerAsync</c>.
    /// </summary>
    internal ValueTask<IListener> BuildListenerAsync(PulsarEndpoint endpoint, IReceiver receiver,
        IWolverineRuntime runtime)
    {
        if (Tenants.Any() && endpoint.TenancyBehavior == TenancyBehavior.TenantAware)
        {
            var compound = new CompoundListener(endpoint.Uri);
            compound.Inner.Add(buildSingleListener(endpoint, receiver, this, runtime));

            foreach (var tenant in Tenants)
            {
                var wrapped = new ReceiverWithRules(receiver, [new TenantIdRule(tenant.TenantId)]);
                compound.Inner.Add(buildSingleListener(endpoint, wrapped, tenant.Transport, runtime));
            }

            return ValueTask.FromResult<IListener>(compound);
        }

        return ValueTask.FromResult(buildSingleListener(endpoint, receiver, this, runtime));
    }

    private static IListener buildSingleListener(PulsarEndpoint endpoint, IReceiver receiver,
        PulsarTransport transport, IWolverineRuntime runtime)
    {
        // Hot-tail (GH-3184): a non-durable Reader at the tail rather than a durable subscription.
        if (endpoint.IsHotTail)
        {
            return new PulsarReaderListener(runtime, endpoint, receiver, transport, runtime.Cancellation);
        }

        return new PulsarListener(runtime, endpoint, receiver, transport, runtime.Cancellation);
    }
}
