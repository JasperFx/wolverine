using System.Data.Common;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
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

        if (!await TryAttainLockAsync(externalTable.AdvisoryLock, conn, token))
        {
            return;
        }

        try
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
        finally
        {
            // GH-5676. The lock is session scoped, and closing the connection does not end the session: it goes
            // back to the pool still holding the lock until the pool resets or prunes it. Every other poller of
            // this table -- another node, or the next host in the same process -- was locked out until then
            await releasePollingLockAsync(externalTable.AdvisoryLock, conn);
        }

        await conn.CloseAsync();
    }

    private async Task releasePollingLockAsync(int lockId, T conn)
    {
        try
        {
            await ReleaseLockAsync(lockId, conn, CancellationToken.None);
        }
        catch (Exception e)
        {
            // Not thrown over whatever failed in the poll itself. The pool's reset releases it eventually
            Logger.LogError(e, "Unable to release the external table polling lock {LockId}", lockId);
        }
    }


    public abstract ITable AddExternalMessageTable(ExternalMessageTable definition);

    protected abstract Task deleteManyAsync(DbTransaction tx, Guid[] ids, DbObjectName tableName, string mapperIdColumnName, CancellationToken token);

    protected abstract DbCommand buildFetchSql(T conn, DbObjectName tableName, string[] columnNames, int maxRecords);

    public abstract Task PublishMessageToExternalTableAsync(ExternalMessageTable table, string? messageTypeName,
        byte[] json,
        CancellationToken token);
}