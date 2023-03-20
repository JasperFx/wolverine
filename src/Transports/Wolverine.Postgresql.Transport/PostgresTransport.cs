using JasperFx.Core;
using Wolverine.Runtime;
using Wolverine.Transports.Postgresql.Internal;

namespace Wolverine.Transports.Postgresql;

public sealed class PostgresTransport : BrokerTransport<PostgresEndpoint>, IAsyncDisposable
{
    public const string ProtocolName = "postgresql";

    private readonly Lazy<PostgresClient> _client;

    public PostgresTransport() : base(ProtocolName, "Azure Service Bus")
    {
        Queues = new(name => new PostgresQueue(this, new QueueDefinition(name)));
        //Topics = new(name => new PostgresTopic(this, new TopicDefinition(name)));

        _client =
            new Lazy<PostgresClient>(() => new PostgresClient(ConnectionString));

        IdentifierDelimiter = ".";
    }

    internal PostgresClient Client => _client.Value;

    public LightweightCache<string, PostgresQueue> Queues { get; }

    public LightweightCache<string, PostgresTopic> Topics { get; }

    public string? ConnectionString { get; set; }

    public Guid Id { get; set; } = Guid.NewGuid();

    public ValueTask DisposeAsync()
    {
        if (_client.IsValueCreated)
        {
            return _client.Value.DisposeAsync();
        }

        return ValueTask.CompletedTask;
    }

    protected override IEnumerable<PostgresEndpoint> endpoints()
    {
        foreach (var queue in Queues) yield return queue;

//        foreach (var topic in Topics) yield return topic;
    }

    protected override PostgresEndpoint findEndpointByUri(Uri uri)
    {
        switch (uri.Host)
        {
            case "queue":
                return Queues[uri.Segments[1]];

            case "topic":
                var topicName = uri.Segments[1].TrimEnd('/');

                return Topics[topicName];
        }

        throw new ArgumentOutOfRangeException(nameof(uri));
    }

    public override ValueTask ConnectAsync(IWolverineRuntime runtime)
    {
        // we're going to use a client per endpoint
        return ValueTask.CompletedTask;
    }

    public override IEnumerable<PropertyColumn> DiagnosticColumns()
    {
        yield return new PropertyColumn("Queue", "Name");
    }
}
