using System.Data.Common;
using JasperFx.Events.Daemon;
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

#pragma warning disable VSTHRD002 // Avoid problematic synchronous waits
            database = tenantedMessageStore.Source.FindAsync(context.TenantId!).GetAwaiter().GetResult() as IMessageDatabase;
#pragma warning restore VSTHRD002 // Avoid problematic synchronous waits
            return database != null;
        }

        return false;
    }
}

public interface IMessageDatabase : IMessageStoreWithAgentSupport, ITenantDatabaseRegistry
{
    public DatabaseSettings Settings {get; }

    string SchemaName { get; set; }

    DbDataSource DataSource { get; }
    ILogger Logger { get; }

    Task StoreIncomingAsync(DbTransaction tx, Envelope[] envelopes);
    Task StoreOutgoingAsync(DbTransaction tx, Envelope[] envelopes);

    /// <summary>
    /// Mark an already-persisted incoming envelope as handled on a caller-supplied connection and
    /// transaction — used by <c>EfCoreEnvelopeTransaction.CommitAsync</c> to close out a durable-inbox
    /// message inside the application's own EF Core transaction. Provider-aware: the default binds the
    /// envelope id through the generic Weasel path (fine for every provider whose driver accepts
    /// <c>DbType.Guid</c>), and Oracle overrides it because its <c>RAW(16)</c> id columns require the
    /// Guid to be bound as <c>byte[]</c> (GH-3581 — the generic path throws "Value does not fall within
    /// the expected range" on ODP.NET).
    /// </summary>
    Task MarkIncomingEnvelopeAsHandledInTransactionAsync(DbConnection conn, DbTransaction? tx, Envelope envelope,
        DateTimeOffset keepUntil, CancellationToken cancellation)
    {
        var cmd = conn.CreateCommand(
                $"update {SchemaName}.{DatabaseConstants.IncomingTable} set {DatabaseConstants.Status} = '{EnvelopeStatus.Handled}', {DatabaseConstants.KeepUntil} = @keep where id = @id")
            .With("id", envelope.Id)
            .With("keep", keepUntil);
        cmd.Transaction = tx;
        return cmd.ExecuteNonQueryAsync(cancellation);
    }

    /// <summary>
    ///     Access the current count of persisted envelopes
    /// </summary>
    /// <returns></returns>
    Task<PersistedCounts> FetchCountsAsync();

    DbCommandBuilder ToCommandBuilder();

    /// <summary>
    /// Builds a provider-specific SQL statement that deletes at most <paramref name="batchSize"/>
    /// expired, successfully handled incoming envelopes in a single statement. The SQL should
    /// expose a single "now" parameter for the cutoff timestamp. Return null if this provider
    /// cannot bound the delete, in which case the cleanup falls back to a single unbounded delete.
    /// </summary>
    string? BatchedDeleteExpiredHandledEnvelopesSql(int batchSize);

    Task EnqueueAsync(IDatabaseOperation operation);
    void WriteLoadScheduledEnvelopeSql(DbCommandBuilder builder, DateTimeOffset utcNow);
    Task PollForScheduledMessagesAsync(IWolverineRuntime runtime, ILogger runtimeLogger,
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