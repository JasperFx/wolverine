using System.Data.Common;
using JasperFx.Core;
using Weasel.Core;
using Wolverine.RDBMS.Transport;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.RDBMS;

public abstract partial class MessageDatabase<T>
{
    public abstract Task MigrateExternalMessageTable(ExternalMessageTable definition);
    
    public async Task PollForMessagesFromExternalTablesAsync(IListener listener,
        IWolverineRuntime runtime,
        ExternalMessageTable externalTable, IReceiver receiver,
        CancellationToken token)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(token);

        if (await TryAttainLockAsync(externalTable.AdvisoryLock, conn, token))
        {
            var command = buildFetchSql(conn, externalTable.TableName, externalTable.Columns().ToArray(),
                externalTable.MessageBatchSize);

            await using var reader = await command.ExecuteReaderAsync(token);
            var envelopes = await externalTable.ReadAllAsync(reader, token);

            await reader.CloseAsync();
            
            if (envelopes.Any())
            {
                // Important to make every envelope as being owned by the
                // current node
                foreach (var envelope in envelopes)
                {
                    envelope.Status = EnvelopeStatus.Incoming;
                    envelope.OwnerId = runtime.DurabilitySettings.AssignedNodeNumber;
                }

                var tx = await conn.BeginTransactionAsync(token);
                await StoreIncomingAsync(tx, envelopes);

                await deleteMany(tx, envelopes.Select(x => x.Id).ToArray(), externalTable.TableName,
                    externalTable.IdColumnName);
                await tx.CommitAsync(token);

                await receiver.ReceivedAsync(listener, envelopes);
            }
        }

        await conn.CloseAsync();
    }


    public abstract ISchemaObject AddExternalMessageTable(ExternalMessageTable definition);

    protected abstract Task deleteMany(DbTransaction tx, Guid[] ids, DbObjectName tableName, string mapperIdColumnName);

    protected abstract Task<bool> TryAttainLockAsync(int lockId, T connection, CancellationToken token);

    protected abstract DbCommand buildFetchSql(T conn, DbObjectName tableName, string[] columnNames, int maxRecords);

    public abstract Task PublishMessageToExternalTableAsync(ExternalMessageTable table, string messageTypeName,
        byte[] json,
        CancellationToken token);
    

}