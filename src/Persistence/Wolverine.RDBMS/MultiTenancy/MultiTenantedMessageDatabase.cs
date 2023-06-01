using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Logging;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Util.Dataflow;

namespace Wolverine.RDBMS.MultiTenancy;

/// <summary>
/// Source of known tenant databases
/// </summary>
public interface IMessageDatabaseSource
{
    ValueTask<IMessageDatabase> FindDatabaseAsync(string tenantId);
    Task InitializeAsync();

    IReadOnlyList<IMessageDatabase> AllActive();
}

public class MultiTenantedMessageDatabase : IMessageStore, IMessageInbox, IMessageOutbox, IMessageStoreAdmin
{
    private readonly ILogger _logger;
    private readonly IMessageDatabaseSource _databases;
    private readonly RetryBlock<IEnvelopeCommand> _retryBlock;


    public MultiTenantedMessageDatabase(IMessageDatabase master, IWolverineRuntime runtime, ILogger logger, IMessageDatabaseSource databases)
    {
        _logger = logger;
        _databases = databases;

        _retryBlock = new RetryBlock<IEnvelopeCommand>((command, cancellation) => command.ExecuteAsync(cancellation),
            _logger, runtime.Cancellation);

        Master = master;
    }
    

    public IMessageDatabase Master { get;}

    public IReadOnlyList<IMessageDatabase> ActiveDatabases() =>
        databases().ToArray();

    public ValueTask<IMessageDatabase> GetDatabaseAsync(string? tenantId)
    {
        return tenantId.IsEmpty() 
            ? new ValueTask<IMessageDatabase>(Master) 
            : _databases.FindDatabaseAsync(tenantId);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var database in databases())
        {
            await database.DisposeAsync();
        }
    }

    public async Task InitializeAsync(IWolverineRuntime runtime)
    {
        await _databases.InitializeAsync();
        await Master.InitializeAsync(runtime);
    }

    public IMessageInbox Inbox => this;
    public IMessageOutbox Outbox => this;
    public INodeAgentPersistence Nodes => Master.Nodes;
    public IMessageStoreAdmin Admin => this;
    public void Describe(TextWriter writer)
    {
        Master.Describe(writer);
    }

    public async Task<ErrorReport?> LoadDeadLetterEnvelopeAsync(Guid id)
    {
        foreach (var database in databases())
        {
            var report = await database.LoadDeadLetterEnvelopeAsync(id);
            if (report != null) return report;
        }

        return null;
    }

    private IEnumerable<IMessageDatabase> databases()
    {
        yield return Master;

        foreach (var database in _databases.AllActive())
        {
            yield return database;
        }
    }

    private async Task executeOnAllAsync(Func<IMessageDatabase, Task> action)
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

        if (exceptions.Any()) throw new AggregateException(exceptions);
    }

    public Task DrainAsync()
    {
        return executeOnAllAsync(d => d.DrainAsync());
    }

    async Task IMessageInbox.ScheduleExecutionAsync(Envelope envelope)
    {
        var database = await GetDatabaseAsync(envelope.TenantId);
        await database.Inbox.ScheduleExecutionAsync(envelope);
    }

    Task IMessageInbox.MoveToDeadLetterStorageAsync(Envelope envelope, Exception? exception)
    {
        return Master.Inbox.MoveToDeadLetterStorageAsync(envelope, exception);
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
                _logger.LogError(e, "Encountered unknown tenant {TenantId} while trying to store incoming envelopes", group.Key);
            }
        }
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
                _logger.LogError(e, "Encountered unknown tenant {TenantId} while trying to store incoming envelopes", group.Key);
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
        var discardGroups = discards.GroupBy(x => x.TenantId).ToArray();
        var reassignedGroups = reassigned.GroupBy(x => x.TenantId).ToArray();

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
        
        foreach (var database in databases())
        {
            try
            {
                size += await database.Admin.MarkDeadLetterEnvelopesAsReplayableAsync(exceptionType);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error trying to mark dead letter envelopes as replayable for database {Name}", database.Name);
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

        foreach (var database in databases())
        {
            var db = await database.FetchCountsAsync();
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

    Task IMessageStoreAdmin.CheckConnectivityAsync(CancellationToken token)
    {
        return executeOnAllAsync(d => d.Admin.CheckConnectivityAsync(token));
    }

    Task IMessageStoreAdmin.MigrateAsync()
    {
        return executeOnAllAsync(d => d.Admin.MigrateAsync());
    }
}
