using System.Data.Common;
using JasperFx.MultiTenancy;
using Microsoft.Extensions.Logging;
using Weasel.Core;
using Wolverine.Logging;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS.MultiTenancy;
using Wolverine.RDBMS.Polling;
using Wolverine.RDBMS.Transport;
using Wolverine.Runtime;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports;
using DbCommandBuilder = Weasel.Core.DbCommandBuilder;

namespace Wolverine.RDBMS;

public static class MessageDatabaseExtensions
{
    /// <summary>
    /// Try to find the right IMessageDatabase for the current MessageContext including its tenant if any
    /// </summary>
    /// <param name="context"></param>
    /// <param name="database"></param>
    /// <returns></returns>
    public static bool TryFindMessageDatabase(this MessageContext context, out IMessageDatabase? database)
    {
        database = default!;
        
        if (context.Storage is IMessageDatabase db)
        {
            database = db;
            return true;
        }

        if (context.Storage is MultiTenantedMessageStore tenantedMessageStore)
        {
            if (context.IsDefaultTenant() && tenantedMessageStore.Main is IMessageDatabase db2)
            {
                database = db2;
                return true;
            }

            database = tenantedMessageStore.Source.FindAsync(context.TenantId).GetAwaiter().GetResult() as IMessageDatabase;
            return database != null;
        }

        return false;
    }
}

public interface IMessageDatabase : IMessageStoreWithAgentSupport, ITenantDatabaseRegistry
{
    public DatabaseSettings Settings {get; }
    
    bool IsMain { get; }

    string SchemaName { get; set; }

    DbDataSource DataSource { get; }
    ILogger Logger { get; }

    Task StoreIncomingAsync(DbTransaction tx, Envelope[] envelopes);
    Task StoreOutgoingAsync(DbTransaction tx, Envelope[] envelopes);

    /// <summary>
    ///     Access the current count of persisted envelopes
    /// </summary>
    /// <returns></returns>
    Task<PersistedCounts> FetchCountsAsync();

    DbCommandBuilder ToCommandBuilder();

    Task EnqueueAsync(IDatabaseOperation operation);
    void WriteLoadScheduledEnvelopeSql(DbCommandBuilder builder, DateTimeOffset utcNow);
    Task PollForScheduledMessagesAsync(ILocalReceiver localQueue, ILogger runtimeLogger,
        DurabilitySettings durabilitySettings,
        CancellationToken cancellationToken);

    IAdvisoryLock AdvisoryLock { get; }

    Task PollForMessagesFromExternalTablesAsync(IListener listener,
        IWolverineRuntime settings, ExternalMessageTable externalTable,
        IReceiver receiver,
        CancellationToken token);

    Task MigrateExternalMessageTable(ExternalMessageTable messageTable);

    Task PublishMessageToExternalTableAsync(ExternalMessageTable table, string messageTypeName, byte[] json, CancellationToken token);
    
}