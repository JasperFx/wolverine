using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Logging;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Util.Dataflow;

namespace Wolverine.RDBMS.MultiTenancy;

public class UnknownTenantException : Exception
{
    public UnknownTenantException(string tenantId) : base($"Unknown tenant id {tenantId}")
    {
    }
}

public class MultiTenantedMessageDatabase : IMessageStore, IMessageInbox, IMessageOutbox, IMessageStoreAdmin
{
    private readonly ILogger<MultiTenantedMessageDatabase> _logger;
    private IMessageDatabase _master;
    private ImHashMap<string, IMessageDatabase> _databases = ImHashMap<string, IMessageDatabase>.Empty;
    private readonly RetryBlock<IEnvelopeCommand> _retryBlock;


    public MultiTenantedMessageDatabase(IWolverineRuntime runtime, ILogger<MultiTenantedMessageDatabase> logger)
    {
        _logger = logger;

        _retryBlock = new RetryBlock<IEnvelopeCommand>((command, cancellation) => command.ExecuteAsync(cancellation),
            _logger, runtime.Cancellation);
    }

    public void AddDatabase(IMessageDatabase database)
    {
        _databases = _databases.AddOrUpdate(database.Name, database);
    }

    public void SetDefault(IMessageDatabase database)
    {
        _master = database;
    }

    private IMessageDatabase findDatabase(string? tenantId)
    {
        if (tenantId.IsEmpty()) return _master;

        if (_databases.TryFind(tenantId, out var database)) return database;

        throw new UnknownTenantException(tenantId);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var entry in _databases.Enumerate())
        {
            await entry.Value.DisposeAsync();
        }
    }

    public Task InitializeAsync(IWolverineRuntime runtime)
    {
        return executeOnAllAsync(d => d.InitializeAsync(runtime));
    }

    public IMessageInbox Inbox => this;
    public IMessageOutbox Outbox => this;
    public INodeAgentPersistence Nodes => _master.Nodes;
    public IMessageStoreAdmin Admin => this;
    public void Describe(TextWriter writer)
    {
        _master.Describe(writer);
    }

    // TODO -- what if dead letter queue is only in the master database?
    public async Task<ErrorReport?> LoadDeadLetterEnvelopeAsync(Guid id)
    {
        foreach (var entry in _databases.Enumerate())
        {
            var env = await entry.Value.LoadDeadLetterEnvelopeAsync(id);
            if (env != null) return env;
        }

        return null;
    }

    private async Task executeOnAllAsync(Func<IMessageDatabase, Task> action)
    {
        var exceptions = new List<Exception>();

        foreach (var entry in _databases.Enumerate())
        {
            try
            {
                await action(entry.Value);
            }   
            catch (Exception e)
            {
                exceptions.Add(e);
            }
        }

        if (exceptions.Any()) throw new AggregateException(exceptions);
    }

    public Task DrainAsync()
    {
        return executeOnAllAsync(d => d.DrainAsync());
    }

    Task IMessageInbox.ScheduleExecutionAsync(Envelope envelope)
    {
        return findDatabase(envelope.TenantId).Inbox.ScheduleExecutionAsync(envelope);
    }

    Task IMessageInbox.MoveToDeadLetterStorageAsync(Envelope envelope, Exception? exception)
    {
        return _master.Inbox.MoveToDeadLetterStorageAsync(envelope, exception);
    }

    Task IMessageInbox.IncrementIncomingEnvelopeAttemptsAsync(Envelope envelope)
    {
        return findDatabase(envelope.TenantId).Inbox.IncrementIncomingEnvelopeAttemptsAsync(envelope);
    }

    Task IMessageInbox.StoreIncomingAsync(Envelope envelope)
    {
        return findDatabase(envelope.TenantId).Inbox.StoreIncomingAsync(envelope);
    }

    async Task IMessageInbox.StoreIncomingAsync(IReadOnlyList<Envelope> envelopes)
    {
        var groups = envelopes.GroupBy(x => x.TenantId).ToArray();

        if (groups.Length == 1)
        {
            await findDatabase(groups[0].Key).Inbox.StoreIncomingAsync(envelopes);
            return;
        }
        
        foreach (var group in groups)    
        {
            try
            {
                var database = findDatabase(group.Key);
                var command = new StoreIncomingAsyncGroup(database, group.ToArray());
                await _retryBlock.PostAsync(command);
            }
            catch (UnknownTenantException e)
            {
                _logger.LogError(e, "Encountered unknown tenant {TenantId} while trying to store incoming envelopes", group.Key);
            }
        }
    }

    Task IMessageInbox.ScheduleJobAsync(Envelope envelope)
    {
        return findDatabase(envelope.TenantId).Inbox.ScheduleJobAsync(envelope);
    }

    Task IMessageInbox.MarkIncomingEnvelopeAsHandledAsync(Envelope envelope)
    {
        return findDatabase(envelope.TenantId).Inbox.MarkIncomingEnvelopeAsHandledAsync(envelope);
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

    Task IMessageOutbox.StoreOutgoingAsync(Envelope envelope, int ownerId)
    {
        return findDatabase(envelope.TenantId).Outbox.StoreOutgoingAsync(envelope, ownerId);
    }

    async Task IMessageOutbox.DeleteOutgoingAsync(Envelope[] envelopes)
    {
        var groups = envelopes.GroupBy(x => x.TenantId).ToArray();

        if (groups.Length == 1)
        {
            await findDatabase(groups[0].Key).Outbox.DeleteOutgoingAsync(envelopes);
            return;
        }
        
        foreach (var group in groups)    
        {
            try
            {
                var database = findDatabase(group.Key);
                var command = new DeleteOutgoingAsyncGroup(database, group.ToArray());
                await _retryBlock.PostAsync(command);
            }
            catch (UnknownTenantException e)
            {
                _logger.LogError(e, "Encountered unknown tenant {TenantId} while trying to store incoming envelopes", group.Key);
            }
        }
    }

    Task IMessageOutbox.DeleteOutgoingAsync(Envelope envelope)
    {
        return findDatabase(envelope.TenantId).Outbox.DeleteOutgoingAsync(envelope);
    }

    async Task IMessageOutbox.DiscardAndReassignOutgoingAsync(Envelope[] discards, Envelope[] reassigned, int nodeId)
    {
        var discardGroups = discards.GroupBy(x => x.TenantId).ToArray();
        var reassignedGroups = reassigned.GroupBy(x => x.TenantId).ToArray();

        var dict = new Dictionary<string, DiscardAndReassignOutgoingAsyncGroup>();

        foreach (var group in discardGroups)
        {
            if (_databases.TryFind(group.Key, out var database))
            {
                var command = new DiscardAndReassignOutgoingAsyncGroup(database, nodeId);
                dict[group.Key] = command;
                
                command.AddDiscards(group);
            }
            else
            {
                _logger.LogInformation("Encountered unknown tenant {TenantId} while trying to discard or reassign outgoing envelopes", group.Key);
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
                if (_databases.TryFind(group.Key, out var database))
                {
                    command = new DiscardAndReassignOutgoingAsyncGroup(database, nodeId);
                    dict[group.Key] = command;
                
                    command.AddReassigns(group);
                }
                else
                {
                    _logger.LogInformation("Encountered unknown tenant {TenantId} while trying to discard or reassign outgoing envelopes", group.Key);
                }
            }
        }

        foreach (var value in dict.Values)
        {
            await _retryBlock.PostAsync(value);
        }
    }

    Task IMessageStoreAdmin.ClearAllAsync()
    {
        return executeOnAllAsync(d => d.Admin.ClearAllAsync());
    }

    async Task<int> IMessageStoreAdmin.MarkDeadLetterEnvelopesAsReplayableAsync(string exceptionType)
    {
        var size = 0;

        foreach (var entry in _databases.Enumerate())
        {
            try
            {
                size += await entry.Value.Admin.MarkDeadLetterEnvelopesAsReplayableAsync(exceptionType);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error trying to mark dead letter envelopes as replayable");
            }
        }

        return size;
    }

    Task IMessageStoreAdmin.RebuildAsync()
    {
        return executeOnAllAsync(d => d.Admin.RebuildAsync());
    }

    async Task<PersistedCounts> IMessageStoreAdmin.FetchCountsAsync()
    {
        var counts = new PersistedCounts();

        foreach (var entry in _databases.Enumerate())
        {
            var db = await entry.Value.FetchCountsAsync();
            counts.Add(db);
        }

        return counts;
    }

    async Task<IReadOnlyList<Envelope>> IMessageStoreAdmin.AllIncomingAsync()
    {
        var list = new List<Envelope>();
        
        foreach (var entry in _databases.Enumerate())
        {
            var envelopes = await entry.Value.Admin.AllIncomingAsync();
            list.AddRange(envelopes);
        }

        return list;
    }

    async Task<IReadOnlyList<Envelope>> IMessageStoreAdmin.AllOutgoingAsync()
    {
        var list = new List<Envelope>();
        
        foreach (var entry in _databases.Enumerate())
        {
            var envelopes = await entry.Value.Admin.AllOutgoingAsync();
            list.AddRange(envelopes);
        }

        return list;
    }

    Task IMessageStoreAdmin.ReleaseAllOwnershipAsync()
    {
        return executeOnAllAsync(d => d.Admin.ReleaseAllOwnershipAsync());
    }

    Task IMessageStoreAdmin.CheckConnectivityAsync(CancellationToken token)
    {
        return executeOnAllAsync(d => d.Admin.CheckConnectivityAsync(token));
    }

    Task IMessageStoreAdmin.MigrateAsync()
    {
        return executeOnAllAsync(d => d.Admin.MigrateAsync());
    }
}
