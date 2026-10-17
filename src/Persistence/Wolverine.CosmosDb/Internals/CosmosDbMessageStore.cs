using System.Net;
using JasperFx.Core.Reflection;
using JasperFx.Descriptors;
using Microsoft.Azure.Cosmos;
using Wolverine.CosmosDb.Internals.Durability;
using Wolverine.CosmosDb.Internals.Transport;
using Wolverine.Persistence;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Transports;

namespace Wolverine.CosmosDb.Internals;

public partial class CosmosDbMessageStore : IMessageStoreWithAgentSupport
{
    private readonly Container _container;
    private readonly CosmosClient _client;
    private readonly WolverineOptions _options;
    private readonly string _databaseName;
    private readonly Func<Envelope, string> _identity;

    public CosmosDbMessageStore(CosmosClient client, string databaseName, Container container,
        WolverineOptions options)
    {
        _client = client;
        _databaseName = databaseName;
        _container = container;
        _options = options;

        _identity = options.Durability.MessageIdentity == MessageIdentity.IdOnly
            ? e => $"incoming|{e.Id}"
            : e =>
                $"incoming|{e.Id}|{CosmosSafeId(e.Destination?.ToString().Replace(":/", "").TrimEnd('/'))}";

        _leaderLockId = $"lock|leader";
        _scheduledLockId = $"lock|scheduled";
    }

    public MessageStoreRole Role { get; set; } = MessageStoreRole.Main;

    public List<string> TenantIds { get; } = new();

    public void PromoteToMain(IWolverineRuntime runtime)
    {
        Role = MessageStoreRole.Main;
    }

    public void DemoteToAncillary()
    {
        Role = MessageStoreRole.Ancillary;
    }

    public string Name => _databaseName;

    public Uri Uri => new($"{PersistenceConstants.AgentScheme}://cosmosdb/durability");

    public string IdentityFor(Envelope envelope) => _identity(envelope);

    // GH-4216. Cosmos DB does not allow '/', '\\', '?' or '#' in a document id: a create succeeds, but point reads,
    // replaces and deletes by that id miss, so every update under MessageIdentity.IdAndDestination -- whose
    // identity carried the destination's slashes -- quietly fell through to "not found". '|' is already the
    // separator inside the identity
    internal static string? CosmosSafeId(string? value)
    {
        return value?.Replace('/', '|').Replace('\\', '|').Replace('?', '|').Replace('#', '|');
    }

    public ValueTask DisposeAsync()
    {
        HasDisposed = true;
        return new ValueTask();
    }

    public bool HasDisposed { get; set; }
    public IMessageInbox Inbox => this;
    public IMessageOutbox Outbox => this;
    public INodeAgentPersistence Nodes => this;

    // Default no-op listener store. CosmosDB-backed listener registry is a
    // follow-up implementation; stays a no-op while EnableDynamicListeners
    // is false (default).
    public IListenerStore Listeners { get; protected set; } = NullListenerStore.Instance;

    public IMessageStoreAdmin Admin => this;
    public IDeadLetters DeadLetters => this;

    public void Initialize(IWolverineRuntime runtime)
    {
        if (Role == MessageStoreRole.Main
            && runtime.Options.Transports.NodeControlEndpoint == null
            && runtime.Options.Durability.Mode == DurabilityMode.Balanced)
        {
            var transport = new CosmosDbControlTransport(_container, runtime.Options);
            runtime.Options.Transports.Add(transport);
            runtime.Options.Transports.NodeControlEndpoint = transport.ControlEndpoint;
        }
    }

    public DatabaseDescriptor Describe()
    {
        return new DatabaseDescriptor(this)
        {
            Engine = "cosmosdb",
            DatabaseName = _databaseName,

            // The container plays the part a schema does for the relational stores: two applications sharing a
            // database are told apart by it
            SchemaOrNamespace = _container.Id
        };
    }

    public Task DrainAsync()
    {
        return Task.CompletedTask;
    }

    public IAgent StartScheduledJobs(IWolverineRuntime runtime)
    {
        _leaderLockId = $"lock|leader|{runtime.Options.ServiceName.ToLowerInvariant()}";
        _scheduledLockId = $"lock|scheduled|{runtime.Options.ServiceName.ToLowerInvariant()}";
        _runtime = runtime;

        // NodeAgentController owns the durability agent lifecycle via the
        // wolverinedb://cosmosdb/durability URI (built through
        // MessageStoreCollection.BuildAgentAsync and started with StartAsync); do not
        // start a second instance here. The agent built here is held by
        // WolverineRuntime.DurableScheduledJobs purely for its disposal-time StopAsync,
        // which is null-safe on the unstarted task fields. Eagerly calling StartTimers()
        // would produce two concurrent pollers sharing this store — the #2623 bug.
        return BuildAgent(runtime);
    }

    public IAgent BuildAgent(IWolverineRuntime runtime)
    {
        return new CosmosDbDurabilityAgent(_container, runtime, this);
    }

    public IAgentFamily? BuildAgentFamily(IWolverineRuntime runtime)
    {
        return null;
    }

    public async Task<IReadOnlyList<Envelope>> LoadPageOfGloballyOwnedIncomingAsync(Uri listenerAddress, int limit)
    {
        var partitionKey = listenerAddress.ToString();
        var queryText =
            "SELECT * FROM c WHERE c.docType = @docType AND c.ownerId = @ownerId AND c.receivedAt = @receivedAt AND c.status = @status ORDER BY c.envelopeId OFFSET 0 LIMIT @limit";
        var query = new QueryDefinition(queryText)
            .WithParameter("@docType", DocumentTypes.Incoming)
            .WithParameter("@ownerId", TransportConstants.AnyNode)
            .WithParameter("@receivedAt", listenerAddress.ToString())
            .WithParameter("@status", EnvelopeStatus.Incoming)
            .WithParameter("@limit", limit);

        // GH-5059. Under IdOnly a document is partitioned by its identity, not by the address it was received at, and
        // documents written before that are still in the address's partition, so the query runs across partitions.
        // Under IdAndDestination every document for the address is in its partition
        var results = new List<Envelope>();
        using var iterator = _container.GetItemQueryIterator<IncomingMessage>(query,
            requestOptions: _options.Durability.MessageIdentity == MessageIdentity.IdOnly
                ? null
                : new QueryRequestOptions { PartitionKey = new PartitionKey(partitionKey) });

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response.Select(x => x.Read()));
        }

        return results;
    }

    public async Task ReassignIncomingAsync(int ownerId, IReadOnlyList<Envelope> incoming)
    {
        foreach (var envelope in incoming)
        {
            // Envelope was already handled/deleted, skip it
            if (await readIncomingAsync(envelope) is not var (message, partitionKey)) continue;

            message.OwnerId = ownerId;
            await replaceIncomingAsync(message, partitionKey);
        }
    }

    public async Task<IReadOnlyList<Envelope>> TryClaimIncomingAsync(int ownerId, IReadOnlyList<Envelope> incoming)
    {
        var claimed = new List<Envelope>(incoming.Count);

        try
        {
            foreach (var envelope in incoming)
            {
                if (await readIncomingAsync(envelope, _options.Durability.Cancellation) is not var (message, partitionKey))
                {
                    continue;
                }

                if (message.OwnerId != TransportConstants.AnyNode || message.Status != EnvelopeStatus.Incoming)
                {
                    continue;
                }

                message.OwnerId = ownerId;
                try
                {
                    await _container.ReplaceItemAsync(message, message.Id, new PartitionKey(partitionKey),
                        new ItemRequestOptions { IfMatchEtag = message.ETag }, _options.Durability.Cancellation);
                }
                catch (CosmosException e) when (e.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound)
                {
                    // Another recovery pass updated or deleted this inbox document first.
                    continue;
                }

                envelope.OwnerId = ownerId;
                claimed.Add(envelope);
            }
        }
        catch (Exception claimFailure)
        {
            if (claimed.Count > 0)
            {
                try
                {
                    // The caller cannot enqueue a partial result when this method throws, so release earlier wins.
                    await ReassignIncomingAsync(TransportConstants.AnyNode, claimed);
                }
                catch (Exception releaseFailure)
                {
                    throw new AggregateException(
                        "Cosmos DB failed to claim incoming messages and could not release the partial claim",
                        claimFailure, releaseFailure);
                }
            }

            throw;
        }

        return claimed;
    }
}
