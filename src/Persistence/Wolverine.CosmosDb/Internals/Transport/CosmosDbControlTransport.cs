using JasperFx.Blocks;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Configuration.Capabilities;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.CosmosDb.Internals.Transport;

internal class CosmosDbControlTransport : ITransport, IAsyncDisposable
{
    public const string ProtocolName = "cosmoscontrol";

    private readonly Cache<Guid, CosmosDbControlEndpoint> _endpoints;
    private readonly WolverineOptions _options;
    private RetryBlock<List<Envelope>>? _deleteBlock;

    public CosmosDbControlTransport(Container container, WolverineOptions options)
    {
        Container = container;
        _options = options;

        _endpoints = new Cache<Guid, CosmosDbControlEndpoint>(nodeId =>
        {
            return new CosmosDbControlEndpoint(this, nodeId);
        });

        ControlEndpoint = _endpoints[_options.UniqueNodeId];
        ControlEndpoint.IsListener = true;
    }

    public bool TryBuildBrokerUsage(out BrokerDescription description)
    {
        description = default;
        return false;
    }

    public CosmosDbControlEndpoint ControlEndpoint { get; }

    public Container Container { get; }

    public WolverineOptions Options => _options;

    public async ValueTask DisposeAsync()
    {
        if (_deleteBlock != null)
        {
            try
            {
                await _deleteBlock.DrainAsync();
            }
            catch (TaskCanceledException)
            {
            }

            _deleteBlock.SafeDispose();
        }
    }

    public string Protocol => ProtocolName;
    public string Name => "CosmosDb Control Message Transport for Wolverine Control Messages";

    public Endpoint ReplyEndpoint()
    {
        return _endpoints[_options.UniqueNodeId];
    }

    public Endpoint GetOrCreateEndpoint(Uri uri)
    {
        var nodeId = Guid.Parse(uri.Host);
        return _endpoints[nodeId];
    }

    public Endpoint? TryGetEndpoint(Uri uri)
    {
        var nodeId = Guid.Parse(uri.Host);
        return _endpoints.TryFind(nodeId, out var e) ? e : null;
    }

    public IEnumerable<Endpoint> Endpoints()
    {
        return _endpoints;
    }

    public ValueTask InitializeAsync(IWolverineRuntime runtime)
    {
        foreach (var endpoint in Endpoints()) endpoint.Compile(runtime);

        _deleteBlock = new RetryBlock<List<Envelope>>(deleteEnvelopesAsync,
            runtime.LoggerFactory.CreateLogger<CosmosDbControlTransport>(), runtime.Options.Durability.Cancellation);
        return ValueTask.CompletedTask;
    }

    public bool TryBuildStatefulResource(IWolverineRuntime runtime, out IStatefulResource? resource)
    {
        resource = default;
        return false;
    }

    public Task DeleteEnvelopesAsync(List<Envelope> envelopes, CancellationToken cancellationToken)
    {
        if (_deleteBlock == null)
        {
            throw new InvalidOperationException("The CosmosDbControlTransport has not been initialized");
        }

        return _deleteBlock.PostAsync(envelopes);
    }

    private async Task deleteEnvelopesAsync(List<Envelope> envelopes, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        foreach (var envelope in envelopes)
        {
            try
            {
                // We need the partition key to delete. The envelope was sent to a specific node,
                // so the partition key is based on the current node's ID.
                var partitionKey = ControlMessage.PartitionKeyFor(_options.UniqueNodeId);
                await Container.DeleteItemAsync<ControlMessage>(
                    envelope.Id.ToString(),
                    new PartitionKey(partitionKey),
                    cancellationToken: cancellationToken);
            }
            catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // Already deleted, ignore
            }
            catch (OperationCanceledException)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
            }
        }
    }

    public async Task DeleteExpiredMessagesAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var partitionKey = ControlMessage.PartitionKeyFor(_options.UniqueNodeId);

        var query = new QueryDefinition(
                "SELECT c.id FROM c WHERE c.docType = @docType AND c.nodeId = @nodeId AND c.expires < @now")
            .WithParameter("@docType", DocumentTypes.ControlMessage)
            .WithParameter("@nodeId", _options.UniqueNodeId.ToString())
            .WithParameter("@now", nowUnix);

        using var iterator = Container.GetItemQueryIterator<ControlMessage>(query,
            requestOptions: new QueryRequestOptions
            {
                PartitionKey = new PartitionKey(partitionKey)
            });

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync(cancellationToken);
            foreach (var msg in response)
            {
                try
                {
                    await Container.DeleteItemAsync<ControlMessage>(
                        msg.Id,
                        new PartitionKey(partitionKey),
                        cancellationToken: cancellationToken);
                }
                catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    // Already deleted
                }
            }
        }
    }
}
