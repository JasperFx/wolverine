using System.Net;
using JasperFx.Core.Reflection;
using JasperFx.Descriptors;
using Microsoft.Azure.Cosmos;
using Wolverine.CosmosDb.Internals.Durability;
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
                $"incoming|{e.Id}|{e.Destination?.ToString().Replace(":/", "").TrimEnd('/')}";

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

    public Uri Uri => new("cosmosdb://durability");

    public string IdentityFor(Envelope envelope) => _identity(envelope);

    public ValueTask DisposeAsync()
    {
        HasDisposed = true;
        return new ValueTask();
    }

    public bool HasDisposed { get; set; }
    public IMessageInbox Inbox => this;
    public IMessageOutbox Outbox => this;
    public INodeAgentPersistence Nodes => this;
    public IMessageStoreAdmin Admin => this;
    public IDeadLetters DeadLetters => this;

    public void Initialize(IWolverineRuntime runtime)
    {
        // Container creation is handled during setup/migration
    }

    public DatabaseDescriptor Describe()
    {
        return new DatabaseDescriptor(this)
        {
            Engine = "cosmosdb",
            DatabaseName = _databaseName
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
        var agent = BuildAgent(runtime);
        agent.As<CosmosDbDurabilityAgent>().StartTimers();
        return agent;
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

        var results = new List<Envelope>();
        using var iterator = _container.GetItemQueryIterator<IncomingMessage>(query,
            requestOptions: new QueryRequestOptions
            {
                PartitionKey = new PartitionKey(partitionKey)
            });

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
            var id = _identity(envelope);
            var partitionKey = envelope.Destination?.ToString() ?? DocumentTypes.SystemPartition;
            try
            {
                var response =
                    await _container.ReadItemAsync<IncomingMessage>(id, new PartitionKey(partitionKey));
                var message = response.Resource;
                message.OwnerId = ownerId;
                await _container.ReplaceItemAsync(message, id, new PartitionKey(partitionKey));
            }
            catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
            {
                // Envelope was already handled/deleted, skip it
            }
        }
    }
}
