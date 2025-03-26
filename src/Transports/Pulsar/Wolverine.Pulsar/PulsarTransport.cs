using DotPulsar;
using DotPulsar.Abstractions;
using JasperFx.Core;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.Pulsar;

public class PulsarTransport : TransportBase<PulsarEndpoint>, IAsyncDisposable
{
    public const string ProtocolName = "pulsar";
    public const string DeadLetterTopicName = "wolverine-dead-letter-topic";

    private readonly LightweightCache<Uri, PulsarEndpoint> _endpoints;

    public PulsarTransport() : base(ProtocolName, "Pulsar")
    {
        Builder = PulsarClient.Builder();

        _endpoints =
            new LightweightCache<Uri, PulsarEndpoint>(uri => new PulsarEndpoint(uri, this));
    }

    public PulsarEndpoint this[Uri uri] => _endpoints[uri];

    public IPulsarClientBuilder Builder { get; }

    internal IPulsarClient? Client { get; private set; }
    public DeadLetterTopic DeadLetterTopic { get; } = new(DeadLetterTopicName); // TODO: should we even have a default or just per endpoint based


    private IEnumerable<DeadLetterTopic> enabledDeadLetterTopics()
    {
        if (DeadLetterTopic.Mode != DeadLetterTopicMode.WolverineStorage)
        {
            yield return DeadLetterTopic;
        }

        foreach (var queue in endpoints())
        {
            if (queue.IsPersistent && queue.Role == EndpointRole.Application && queue.DeadLetterTopic != null &&
                queue.DeadLetterTopic.Mode != DeadLetterTopicMode.WolverineStorage)
            {
                yield return queue.DeadLetterTopic;
            }
        }
    }

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



    public PulsarEndpoint EndpointFor(string topicPath)
    {
        var uri = PulsarEndpoint.UriFor(topicPath);
        return this[uri];
    }
}