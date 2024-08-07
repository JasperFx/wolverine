using System.Data.Common;
using Microsoft.Extensions.Logging;
using Weasel.Core;
using Wolverine.Logging;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS.Polling;
using Wolverine.Runtime.WorkerQueues;
using DbCommandBuilder = Weasel.Core.DbCommandBuilder;

namespace Wolverine.RDBMS;

public interface IMessageDatabase : IMessageStore
{
    string Name { get; }

    bool IsMaster { get; }

    string SchemaName { get; set; }
    DatabaseSettings Settings { get; }

    DbDataSource DataSource { get; }
    ILogger Logger { get; }

    Task StoreIncomingAsync(DbTransaction tx, Envelope[] envelopes);
    Task StoreOutgoingAsync(DbTransaction tx, Envelope[] envelopes);

    Task ReassignIncomingAsync(int ownerId, IReadOnlyList<Envelope> incoming);

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
}