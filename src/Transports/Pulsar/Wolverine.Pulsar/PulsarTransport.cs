using DotPulsar;
using DotPulsar.Abstractions;
using JasperFx.Core;
using JasperFx.Descriptors;
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
        return ValueTask.CompletedTask;
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
