using JasperFx.Core;
using JasperFx.Core.Descriptions;
using Microsoft.Extensions.Logging;
using Wolverine.Logging;
using Wolverine.Persistence.Durability.DeadLetterManagement;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Transports;
using Wolverine.Util.Dataflow;

namespace Wolverine.Persistence.Durability;

public class MultiTenantedMessageStore<T> : MultiTenantedMessageStore, IAncillaryMessageStore<T>
{
    public MultiTenantedMessageStore(IMessageStore master, IWolverineRuntime runtime, ITenantedMessageStore source) : base(master, runtime, source)
    {
    }

    public Type MarkerType => typeof(T);
}

public partial class MultiTenantedMessageStore : IMessageStore, IMessageInbox, IMessageOutbox, IMessageStoreAdmin, IDeadLetters
{
    private readonly ILogger _logger;
    private readonly RetryBlock<IEnvelopeCommand> _retryBlock;
    private readonly IWolverineRuntime _runtime;
    private bool _initialized;


    public MultiTenantedMessageStore(IMessageStore master, IWolverineRuntime runtime,
        ITenantedMessageStore source)
    {
        _logger = runtime.LoggerFactory.CreateLogger<MultiTenantedMessageStore>();
        _runtime = runtime;
        Source = source;

        _retryBlock = new RetryBlock<IEnvelopeCommand>((command, cancellation) => command.ExecuteAsync(cancellation),
            _logger, runtime.Cancellation);

        Master = master;
    }

    public ITenantedMessageStore Source { get; }

    public Uri Uri => new Uri($"{PersistenceConstants.AgentScheme}://multitenanted");


    public IMessageStore Master { get; }

    async Task IMessageInbox.ScheduleExecutionAsync(Envelope envelope)
    {
        var database = await GetDatabaseAsync(envelope.TenantId);
        await database.Inbox.ScheduleExecutionAsync(envelope);
    }

    public async Task ReassignIncomingAsync(int ownerId, IReadOnlyList<Envelope> incoming)
    {
        string tenantId = null;
        try
        {
            tenantId = incoming.Select(x => x.TenantId).Distinct().Single();
        }
        catch (Exception e)
        {
            throw new ArgumentOutOfRangeException(nameof(incoming),
                "Invalid in this case to use a mixed bag of tenanted envelopes");
        }
        
        var database = await GetDatabaseAsync(tenantId);
        await database.ReassignIncomingAsync(ownerId, incoming);
    }

    public string Name { get; }

    async Task IMessageInbox.MoveToDeadLetterStorageAsync(Envelope envelope, Exception? exception)
    {
        var database = await GetDatabaseAsync(envelope.TenantId);
        await database.Inbox.MoveToDeadLetterStorageAsync(envelope, exception);
    }

    async Task IMessageInbox.IncrementIncomingEnvelopeAttemptsAsync(Envelope envelope)
    {
        var database = await GetDatabaseAsync(envelope.TenantId);
        await database.Inbox.IncrementIncomingEnvelopeAttemptsAsync(envelope);
    }

    async Task IMessageInbox.StoreIncomingAsync(Envelope envelope)
    {
        var database = await GetDatabaseAsync(envelope.TenantId);
        await database.Inbox.StoreIncomingAsync(envelope);
    }

    async Task IMessageInbox.StoreIncomingAsync(IReadOnlyList<Envelope> envelopes)
    {
        var groups = envelopes.GroupBy(x => x.TenantId).ToArray();

        if (groups.Length == 1)
        {
            var database = await GetDatabaseAsync(groups[0].Key);
            await database.Inbox.StoreIncomingAsync(envelopes);
            return;
        }

        foreach (var group in groups)
        {
            try
            {
                var database = await GetDatabaseAsync(group.Key);
                var command = new StoreIncomingAsyncGroup(database, group.ToArray());
                await _retryBlock.PostAsync(command);
            }
            catch (UnknownTenantException e)
            {
                _logger.LogError(e, "Encountered unknown tenant {TenantId} while trying to store incoming envelopes",
                    group.Key);
            }
        }
    }

    public async Task<IReadOnlyList<Envelope>> LoadPageOfGloballyOwnedIncomingAsync(Uri listenerAddress, int limit)
    {
        // Really just here for diagnostics
        var list = new List<Envelope>();
        foreach (var database in databases())
        {
            list.AddRange(await database.LoadPageOfGloballyOwnedIncomingAsync(listenerAddress, limit));
        }

        return list;
    }

    async Task IMessageInbox.ScheduleJobAsync(Envelope envelope)
    {
        var database = await GetDatabaseAsync(envelope.TenantId);
        await database.Inbox.ScheduleJobAsync(envelope);
    }

    async Task IMessageInbox.MarkIncomingEnvelopeAsHandledAsync(Envelope envelope)
    {
        var database = await GetDatabaseAsync(envelope.TenantId);
        await database.Inbox.MarkIncomingEnvelopeAsHandledAsync(envelope);
    }

    public async Task MarkIncomingEnvelopeAsHandledAsync(IReadOnlyList<Envelope> envelopes)
    {
        var groups = envelopes.GroupBy(x => x.TenantId).ToArray();

        if (groups.Length == 1)
        {
            var database = await GetDatabaseAsync(groups[0].Key);
            await database.Inbox.MarkIncomingEnvelopeAsHandledAsync(envelopes);
            return;
        }

        foreach (var group in groups)
        {
            try
            {
                var database = await GetDatabaseAsync(group.Key);
                await database.Inbox.MarkIncomingEnvelopeAsHandledAsync(group.ToArray());
            }
            catch (UnknownTenantException e)
            {
                _logger.LogError(e, "Encountered unknown tenant {TenantId} while trying to store incoming envelopes",
                    group.Key);
            }
        }
    }

    Task IMessageInbox.ReleaseIncomingAsync(int ownerId)
    {
        return executeOnAllAsync(d => d.Inbox.ReleaseIncomingAsync(ownerId));
    }

    Task IMessageInbox.ReleaseIncomingAsync(int ownerId, Uri receivedAt)
    {
        return executeOnAllAsync(d => d.Inbox.ReleaseIncomingAsync(ownerId, receivedAt));
    }

    Task<IReadOnlyList<Envelope>> IMessageOutbox.LoadOutgoingAsync(Uri destination)
    {
        throw new NotSupportedException();
    }

    async Task IMessageOutbox.StoreOutgoingAsync(Envelope envelope, int ownerId)
    {
        var database = await GetDatabaseAsync(envelope.TenantId);
        await database.Outbox.StoreOutgoingAsync(envelope, ownerId);
    }

    async Task IMessageOutbox.DeleteOutgoingAsync(Envelope[] envelopes)
    {
        var groups = envelopes.GroupBy(x => x.TenantId).ToArray();

        if (groups.Length == 1)
        {
            var database = await GetDatabaseAsync(groups[0].Key);
            await database.Outbox.DeleteOutgoingAsync(envelopes);
            return;
        }

        foreach (var group in groups)
        {
            try
            {
                var database = await GetDatabaseAsync(group.Key);
                var command = new DeleteOutgoingAsyncGroup(database, group.ToArray());
                await _retryBlock.PostAsync(command);
            }
            catch (UnknownTenantException e)
            {
                _logger.LogError(e, "Encountered unknown tenant {TenantId} while trying to store incoming envelopes",
                    group.Key);
            }
        }
    }

    async Task IMessageOutbox.DeleteOutgoingAsync(Envelope envelope)
    {
        var database = await GetDatabaseAsync(envelope.TenantId);
        await database.Outbox.DeleteOutgoingAsync(envelope);
    }

    async Task IMessageOutbox.DiscardAndReassignOutgoingAsync(Envelope[] discards, Envelope[] reassigned, int nodeId)
    {
        var discardGroups = discards.GroupBy(x => x.TenantId ?? TransportConstants.Default).ToArray();
        var reassignedGroups = reassigned.GroupBy(x => x.TenantId ?? TransportConstants.Default).ToArray();

        var dict = new Dictionary<string, DiscardAndReassignOutgoingAsyncGroup>();

        foreach (var group in discardGroups)
        {
            try
            {
                var database = await GetDatabaseAsync(group.Key);
                var command = new DiscardAndReassignOutgoingAsyncGroup(database, nodeId);
                dict[group.Key] = command;

                command.AddDiscards(group);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error trying to resolve a tenant database for {TenantId}", group.Key);
            }
        }

        foreach (var group in reassignedGroups)
        {
            if (dict.TryGetValue(group.Key, out var command))
            {
                command.AddReassigns(group);
            }
            else
            {
                try
                {
                    var database = await GetDatabaseAsync(group.Key);
                    command = new DiscardAndReassignOutgoingAsyncGroup(database, nodeId);
                    dict[group.Key] = command;

                    command.AddReassigns(group);
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Error trying to resolve a tenant database for {TenantId}", group.Key);
                }
            }
        }

        foreach (var value in dict.Values) await _retryBlock.PostAsync(value);
    }

    public async ValueTask DisposeAsync()
    {
        if (HasDisposed) return;
        foreach (var database in databases())
        {
            try
            {
                await database.DisposeAsync();
            }
            // ReSharper disable once EmptyGeneralCatchClause
            catch (Exception)
            {
            }
        }

        HasDisposed = true;
    }

    public void Initialize(IWolverineRuntime runtime)
    {
        InitializeAsync(runtime).GetAwaiter().GetResult();
    }

    public async Task InitializeAsync(IWolverineRuntime runtime)
    {
        if (_initialized)
        {
            return;
        }

        await Source.RefreshAsync();

        foreach (var database in databases())
        {
            database.Initialize(runtime);
        }

        _initialized = true;
    }
    
    public bool HasDisposed { get; private set; }
    public IMessageInbox Inbox => this;
    public IMessageOutbox Outbox => this;
    public IDeadLetters DeadLetters => this;
    public INodeAgentPersistence Nodes => Master.Nodes;
    public IMessageStoreAdmin Admin => this;

    public void Describe(TextWriter writer)
    {
        Master.Describe(writer);
    }

    public DatabaseDescriptor Describe()
    {
        return new DatabaseDescriptor(this)
        {
            DatabaseName = "nullo", Engine = "nullo"
        };
    }

    public Task DrainAsync()
    {
        return executeOnAllAsync(d => d.DrainAsync());
    }

    public IAgent StartScheduledJobs(IWolverineRuntime runtime)
    {
        // TODO -- need to start ancillary stores too.
        // and probably refresh all
        return new CompositeAgent(new Uri("internal://scheduledjobs"),
            Source.AllActive().Select(x => x.StartScheduledJobs(runtime)));
    }

    Task IMessageStoreAdmin.ClearAllAsync()
    {
        return executeOnAllAsync(d => d.Admin.ClearAllAsync());
    }

    async Task<int> IDeadLetters.MarkDeadLetterEnvelopesAsReplayableAsync(string exceptionType)
    {
        var size = 0;

        foreach (var database in databases())
        {
            try
            {
                size += await database.DeadLetters.MarkDeadLetterEnvelopesAsReplayableAsync(exceptionType);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error trying to mark dead letter envelopes as replayable for database {Name}",
                    database.Name);
            }
        }

        return size;
    }

    public async Task MarkDeadLetterEnvelopesAsReplayableAsync(Guid[] ids, string? tenantId = null)
    {
        if (tenantId is { })
        {
            var database = await GetDatabaseAsync(tenantId);
            await database.DeadLetters.MarkDeadLetterEnvelopesAsReplayableAsync(ids);
            return;
        }

        foreach (var database in databases())
        {
            await database.DeadLetters.MarkDeadLetterEnvelopesAsReplayableAsync(ids);
        }
    }

    public async Task DeleteDeadLetterEnvelopesAsync(Guid[] ids, string? tenantId = null)
    {
        if (tenantId is { })
        {
            var database = await GetDatabaseAsync(tenantId);
            await database.DeadLetters.DeleteDeadLetterEnvelopesAsync(ids);
            return;
        }

        foreach (var database in databases())
        {
            await database.DeadLetters.DeleteDeadLetterEnvelopesAsync(ids);
        }
    }

    Task IMessageStoreAdmin.RebuildAsync()
    {
        return executeOnAllAsync(d => d.Admin.RebuildAsync());
    }

    async Task<PersistedCounts> IMessageStoreAdmin.FetchCountsAsync()
    {
        var counts = new PersistedCounts();

        foreach (var database in databases())
        {
            var db = await database.Admin.FetchCountsAsync();
            counts.Tenants[database.Name] = db;
            counts.Add(db);
        }

        return counts;
    }

    async Task<IReadOnlyList<Envelope>> IMessageStoreAdmin.AllIncomingAsync()
    {
        var list = new List<Envelope>();

        foreach (var database in databases())
        {
            var envelopes = await database.Admin.AllIncomingAsync();
            list.AddRange(envelopes);
        }

        return list;
    }

    async Task<IReadOnlyList<Envelope>> IMessageStoreAdmin.AllOutgoingAsync()
    {
        var list = new List<Envelope>();

        foreach (var database in databases())
        {
            var envelopes = await database.Admin.AllOutgoingAsync();
            list.AddRange(envelopes);
        }

        return list;
    }

    Task IMessageStoreAdmin.ReleaseAllOwnershipAsync()
    {
        return executeOnAllAsync(d => d.Admin.ReleaseAllOwnershipAsync());
    }

    Task IMessageStoreAdmin.ReleaseAllOwnershipAsync(int ownerId)
    {
        return executeOnAllAsync(async d =>
        {
            try
            {
                await d.Admin.ReleaseAllOwnershipAsync(ownerId);
            }
            catch (ObjectDisposedException)
            {
                // Can happen when the host is disposed without going through a clean
                // StopAsync()
            }
        });
    }

    Task IMessageStoreAdmin.CheckConnectivityAsync(CancellationToken token)
    {
        return executeOnAllAsync(d => d.Admin.CheckConnectivityAsync(token));
    }

    async Task IMessageStoreAdmin.MigrateAsync()
    {
        if (!_initialized)
        {
            await InitializeAsync(_runtime);
        }

        await executeOnAllAsync(d => d.Admin.MigrateAsync());
    }

    public IReadOnlyList<IMessageStore> ActiveDatabases()
    {
        return databases().ToArray();
    }

    public ValueTask<IMessageStore> GetDatabaseAsync(string? tenantId)
    {
        return tenantId.IsEmpty() || tenantId == TransportConstants.Default || tenantId == "Master"
            ? new ValueTask<IMessageStore>(Master)
            : Source.FindStoreAsync(tenantId);
    }

    private IEnumerable<IMessageStore> databases()
    {
        yield return Master;

        foreach (var database in Source.AllActive()) yield return database;
    }

    private async Task executeOnAllAsync(Func<IMessageStore, Task> action)
    {
        var exceptions = new List<Exception>();

        foreach (var database in databases())
        {
            try
            {
                await action(database);
            }
            catch (Exception e)
            {
                exceptions.Add(e);
            }
        }

        if (exceptions.Count != 0)
        {
            throw new AggregateException(exceptions);
        }
    }

    public async Task<DeadLetterEnvelopesFound> QueryDeadLetterEnvelopesAsync(DeadLetterEnvelopeQueryParameters queryParameters, string? tenantId)
    {
        var database = await GetDatabaseAsync(tenantId);
        return await database.DeadLetters.QueryDeadLetterEnvelopesAsync(queryParameters, tenantId);
    }

    public async Task<DeadLetterEnvelope?> DeadLetterEnvelopeByIdAsync(Guid id, string? tenantId = null)
    {
        if (tenantId is { })
        {
            var database = await GetDatabaseAsync(tenantId);
            return await database.DeadLetters.DeadLetterEnvelopeByIdAsync(id);
        }

        foreach (var database in databases())
        {
            var deadLetterEnvelope = await database.DeadLetters.DeadLetterEnvelopeByIdAsync(id);
            if (deadLetterEnvelope != null)
            {
                return deadLetterEnvelope;
            }
        }

        return null;
    }
    
    public IAgentFamily? BuildAgentFamily(IWolverineRuntime runtime)
    {
        throw new NotImplementedException();
        //return new DurabilityAgentFamily(runtime);
    }
    
    internal interface IEnvelopeCommand
    {
        Task ExecuteAsync(CancellationToken cancellationToken);
    }

    internal class StoreIncomingAsyncGroup : IEnvelopeCommand
    {
        private readonly IMessageStore _store;
        private readonly Envelope[] _envelopes;

        public StoreIncomingAsyncGroup(IMessageStore store, Envelope[] envelopes)
        {
            _store = store;
            _envelopes = envelopes;
        }

        public Task ExecuteAsync(CancellationToken cancellationToken)
        {
            return _store.Inbox.StoreIncomingAsync(_envelopes);
        }
    }

    internal class DeleteOutgoingAsyncGroup : IEnvelopeCommand
    {
        private readonly IMessageStore _store;
        private readonly Envelope[] _envelopes;

        public DeleteOutgoingAsyncGroup(IMessageStore store, Envelope[] envelopes)
        {
            _store = store;
            _envelopes = envelopes;
        }

        public Task ExecuteAsync(CancellationToken cancellationToken)
        {
            return _store.Outbox.DeleteOutgoingAsync(_envelopes);
        }
    }

    internal class DiscardAndReassignOutgoingAsyncGroup : IEnvelopeCommand
    {
        private readonly IMessageStore _store;
        private readonly List<Envelope> _discards = new();
        private readonly int _nodeId;
        private readonly List<Envelope> _reassigned = new();

        public DiscardAndReassignOutgoingAsyncGroup(IMessageStore store, int nodeId)
        {
            _store = store;
            _nodeId = nodeId;
        }

        public Task ExecuteAsync(CancellationToken cancellationToken)
        {
            return _store.Outbox.DiscardAndReassignOutgoingAsync(_discards.ToArray(), _reassigned.ToArray(), _nodeId);
        }

        public void AddDiscards(IEnumerable<Envelope> discards)
        {
            _discards.AddRange(discards);
        }

        public void AddReassigns(IEnumerable<Envelope> reassigns)
        {
            _reassigned.AddRange(reassigns);
        }
    }
}