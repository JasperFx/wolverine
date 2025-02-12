using System.Data.Common;
using Microsoft.Extensions.Logging;
using Weasel.Core;
using Wolverine.Logging;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS.Polling;
using Wolverine.RDBMS.Transport;
using Wolverine.Runtime;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports;
using DbCommandBuilder = Weasel.Core.DbCommandBuilder;

namespace Wolverine.RDBMS;

public interface IMessageDatabase : IMessageStoreWithAgentSupport
{
    string Name { get; }

    bool IsMaster { get; }

    string SchemaName { get; set; }
    DatabaseSettings Settings { get; }

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

    void Enqueue(IDatabaseOperation operation);
    
    IAdvisoryLock AdvisoryLock { get; }

    Task PollForMessagesFromExternalTablesAsync(IListener listener,
        IWolverineRuntime settings, ExternalMessageTable externalTable,
        IReceiver receiver,
        CancellationToken token);

    Task MigrateExternalMessageTable(ExternalMessageTable messageTable);

    Task PublishMessageToExternalTableAsync(ExternalMessageTable table, string messageTypeName, byte[] json, CancellationToken token);
}