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
            await using var command = buildFetchSql(conn, externalTable.TableName, externalTable.Columns().ToArray(),
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
                    
                    // Fix for GH-1225
                    envelope.Source = externalTable.TableName.QualifiedName;
                }

                var tx = await conn.BeginTransactionAsync(token);
                await StoreIncomingAsync(tx, envelopes);

                await deleteManyAsync(tx, envelopes.Select(x => x.Id).ToArray(), externalTable.TableName,
                    externalTable.IdColumnName, token);
                await tx.CommitAsync(token);

                await receiver.ReceivedAsync(listener, envelopes);
            }
        }

        await conn.CloseAsync();
    }


    public abstract ITable AddExternalMessageTable(ExternalMessageTable definition);

    protected abstract Task deleteManyAsync(DbTransaction tx, Guid[] ids, DbObjectName tableName, string mapperIdColumnName, CancellationToken token);

    protected abstract DbCommand buildFetchSql(T conn, DbObjectName tableName, string[] columnNames, int maxRecords);

    public abstract Task PublishMessageToExternalTableAsync(ExternalMessageTable table, string? messageTypeName,
        byte[] json,
        CancellationToken token);
}