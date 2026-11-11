using JasperFx.Core;
using Microsoft.Data.SqlClient;
using Weasel.SqlServer;
using Wolverine.Configuration;
using Wolverine.RDBMS;
using Wolverine.Runtime.Serialization;

namespace Wolverine.SqlServer.Transport;

internal class SqlServerQueueSender : ISqlServerQueueSender
{
    private readonly SqlServerQueue _queue;
    private readonly string _connectionString;

    private readonly string _moveFromOutgoingToQueueSql;
    private readonly string _moveFromOutgoingToScheduledSql;
    private readonly string _writeDirectlyToQueueTableSql;
    private readonly string _writeDirectlyToTheScheduledTable;
    private readonly string _deleteFromIncomingSql;
    private readonly string _moveOrWriteToQueueTableSql;
    private readonly string _moveOrWriteToScheduledTableSql;

    // Strictly for testing
    public SqlServerQueueSender(SqlServerQueue queue) : this(queue, queue.Parent.Settings.ConnectionString!, null)
    {
        Destination = queue.Uri;
    }

    public SqlServerQueueSender(SqlServerQueue queue, string connectionString, string? databaseName)
    {
        _queue = queue;
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));

        Destination = SqlServerQueue.ToUri(queue.Name, databaseName);

        _moveFromOutgoingToQueueSql = $@"
INSERT into {queue.QueueTable.Identifier} ({DatabaseConstants.Id}, {DatabaseConstants.Body}, {DatabaseConstants.MessageType}, {DatabaseConstants.KeepUntil})
SELECT {DatabaseConstants.Id}, {DatabaseConstants.Body}, {DatabaseConstants.MessageType}, {DatabaseConstants.DeliverBy}
FROM
    {queue.Parent.MessageStorageSchemaName}.{DatabaseConstants.OutgoingTable}
WHERE {DatabaseConstants.Id} = @id;
DELETE FROM {queue.Parent.MessageStorageSchemaName}.{DatabaseConstants.OutgoingTable} WHERE {DatabaseConstants.Id} = @id;
";

        _moveFromOutgoingToScheduledSql = $@"
INSERT into {queue.ScheduledTable.Identifier} ({DatabaseConstants.Id}, {DatabaseConstants.Body}, {DatabaseConstants.MessageType}, {DatabaseConstants.ExecutionTime}, {DatabaseConstants.KeepUntil})
SELECT {DatabaseConstants.Id}, {DatabaseConstants.Body}, {DatabaseConstants.MessageType}, @time, {DatabaseConstants.DeliverBy}
FROM
    {queue.Parent.MessageStorageSchemaName}.{DatabaseConstants.OutgoingTable}
WHERE {DatabaseConstants.Id} = @id;
DELETE FROM {queue.Parent.MessageStorageSchemaName}.{DatabaseConstants.OutgoingTable} WHERE {DatabaseConstants.Id} = @id;
";

        _writeDirectlyToQueueTableSql =
            $@"insert into {queue.QueueTable.Identifier} ({DatabaseConstants.Id}, {DatabaseConstants.Body}, {DatabaseConstants.MessageType}, {DatabaseConstants.KeepUntil}) values (@id, @body, @type, @expires)";

        _writeDirectlyToTheScheduledTable = $@"
merge {queue.ScheduledTable.Identifier} as target
using (values (@id, @body, @type, @expires, @time)) as source ({DatabaseConstants.Id}, {DatabaseConstants.Body}, {DatabaseConstants.MessageType}, {DatabaseConstants.KeepUntil}, {DatabaseConstants.ExecutionTime})
on target.id = @id
WHEN MATCHED THEN UPDATE set target.body = @body, target.{DatabaseConstants.ExecutionTime} = @time
WHEN NOT MATCHED THEN INSERT  ({DatabaseConstants.Id}, {DatabaseConstants.Body}, {DatabaseConstants.MessageType}, {DatabaseConstants.KeepUntil}, {DatabaseConstants.ExecutionTime}) values (source.{DatabaseConstants.Id}, source.{DatabaseConstants.Body}, source.{DatabaseConstants.MessageType}, source.{DatabaseConstants.KeepUntil}, source.{DatabaseConstants.ExecutionTime});
";

        _deleteFromIncomingSql =
            $"delete from {queue.Parent.MessageStorageSchemaName}.{DatabaseConstants.IncomingTable} where id = @id";

        // GH-5107. The envelope's outbox row may exist even though WasPersistedInOutbox is false: the EF Core
        // outbox writes it through its own unit of work, and outbox recovery reads it back without the flag.
        // Take the row out of the outbox in the same transaction that puts the envelope on the queue, so a
        // node dying in between can't leave a row behind for recovery to send again. With no outbox row,
        // this is the plain insert.
        var deleteFromOutgoing =
            $"DELETE FROM {queue.Parent.MessageStorageSchemaName}.{DatabaseConstants.OutgoingTable} WHERE {DatabaseConstants.Id} = @id;";

        _moveOrWriteToQueueTableSql = inTransaction($@"
IF NOT EXISTS (SELECT 1 FROM {queue.QueueTable.Identifier} WHERE {DatabaseConstants.Id} = @id)
    {_writeDirectlyToQueueTableSql};
{deleteFromOutgoing}
");

        _moveOrWriteToScheduledTableSql = inTransaction(_writeDirectlyToTheScheduledTable + deleteFromOutgoing);
    }

    private static string inTransaction(string sql)
    {
        return $@"
BEGIN TRY
    BEGIN TRANSACTION;
{sql.Trim()}
    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH
";
    }

    public bool SupportsNativeScheduledSend => true;
    public Uri Destination { get; }

    public async Task<bool> PingAsync()
    {
        try
        {
            await _queue.CheckAsync();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task ScheduleRetryAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        // GH-5426. The retry is fenced on the inbox row's owner. A node ejected as stale while it held the envelope in
        // memory resumes and fails it again after another node has recovered the row; unfenced, it deleted whatever
        // inbox row the other execution held for the id and wrote a second scheduled copy, and both copies ran the
        // retry. The scheduled row is written only if this node's inbox row was deleted, in the same transaction.
        // Only the durable receiver calls this, and every envelope it holds from a database queue has an inbox row
        // owned by this node -- the durable pop, inbox recovery and dead letter replay all write one -- so the fence
        // does not depend on WasPersistedInInbox, which the pop does not set.
        var owner = _queue.Parent.Storage?.Durability.AssignedNodeNumber;

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        try
        {
            // The inbox delete and the scheduled write commit together or not at all. Run as two autocommitted
            // statements, a scheduled write that failed (a timeout, a deadlock) left the inbox row deleted with
            // nothing scheduled, and the message lived on only in this node's memory until the retry block tried
            // again -- a node that died or shut down first lost it. A client-side transaction, rather than
            // BEGIN TRANSACTION in the batch, so a command timeout's attention rolls it back with the dispose.
            await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(cancellationToken);

            var delete = _deleteFromIncomingSql;
            if (owner.HasValue) delete += $" and {DatabaseConstants.OwnerId} = @owner";

            await using (var deleteCmd = conn.CreateCommand(delete).With("id", envelope.Id))
            {
                if (owner.HasValue) deleteCmd.With("owner", owner.Value);
                deleteCmd.Transaction = tx;

                if (await deleteCmd.ExecuteNonQueryAsync(cancellationToken) == 0 && owner.HasValue)
                {
                    // Another execution owns the message now: drop this stale copy
                    await tx.RollbackAsync(cancellationToken);
                    return;
                }
            }

            await using var cmd = conn.CreateCommand(_writeDirectlyToTheScheduledTable)
                .With("id", envelope.Id)
                .With("body", EnvelopeSerializer.Serialize(envelope))
                .With("type", envelope.MessageType!)
                .With("expires", envelope.DeliverBy!)
                .With("time", envelope.ScheduledTime!);
            cmd.Transaction = tx;
            await cmd.ExecuteNonQueryAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    public async ValueTask SendAsync(Envelope envelope)
    {
        if (_queue.Mode == EndpointMode.Durable && envelope.WasPersistedInOutbox)
        {
            if (envelope.IsScheduledForLater(DateTimeOffset.UtcNow))
            {
                await MoveFromOutgoingToScheduledAsync(envelope, CancellationToken.None);
            }
            else
            {
                await MoveFromOutgoingToQueueAsync(envelope, CancellationToken.None);
            }
        }
        else if (_queue.Mode == EndpointMode.Durable)
        {
            await MoveOrWriteToQueueAsync(envelope, CancellationToken.None);
        }
        else
        {
            await SendAsync(envelope, CancellationToken.None);
        }
    }

    /// <summary>
    ///     GH-5107. Puts an envelope on the queue (or the scheduled table) and deletes its outbox row, if it has
    ///     one, in a single transaction
    /// </summary>
    public async Task MoveOrWriteToQueueAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        try
        {
            var scheduled = envelope.IsScheduledForLater(DateTimeOffset.UtcNow);
            await using var cmd = conn.CreateCommand(scheduled ? _moveOrWriteToScheduledTableSql : _moveOrWriteToQueueTableSql)
                .With("id", envelope.Id)
                .With("body", EnvelopeSerializer.Serialize(envelope))
                .With("type", envelope.MessageType!)
                .With("expires", envelope.DeliverBy!);

            if (scheduled)
            {
                cmd.With("time", envelope.ScheduledTime!);
            }

            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqlException e)
        {
            // A concurrent duplicate send won the insert. The transaction rolled back, so the outbox row is still
            // there for DurableSendingAgent to delete, as it always has been
            if (e.Number is 2627 or 2601) return;
            throw;
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    public async Task MoveFromOutgoingToQueueAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        try
        {
            // GH-5438. The INSERT and the DELETE commit together. Autocommitted one by one, a process killed or a
            // command timed out between them left the envelope in the queue AND the outbox, and outbox recovery
            // sent it a second time
            await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(cancellationToken);
            await using var cmd = conn.CreateCommand(_moveFromOutgoingToQueueSql)
                .With("id", envelope.Id);
            cmd.Transaction = tx;
            var count = await cmd.ExecuteNonQueryAsync(cancellationToken);

            if (count == 0) throw new InvalidOperationException("No matching outgoing envelope");

            await tx.CommitAsync(cancellationToken);
        }
        catch (SqlException e)
        {
            // Idempotent on a duplicate send: 2627 = PK violation, 2601 = unique index violation.
            // Match on the error number rather than the message text, which is localized.
            if (e.Number is 2627 or 2601) return;
            throw;
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    public async Task MoveFromOutgoingToScheduledAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        if (!envelope.ScheduledTime.HasValue)
            throw new InvalidOperationException("This envelope has no scheduled time");

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        try
        {
            // GH-5438: the INSERT and the DELETE commit together, as in MoveFromOutgoingToQueueAsync
            await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(cancellationToken);
            await using var cmd = conn.CreateCommand(_moveFromOutgoingToScheduledSql)
                .With("id", envelope.Id)
                .With("time", envelope.ScheduledTime!.Value);
            cmd.Transaction = tx;
            var count = await cmd.ExecuteNonQueryAsync(cancellationToken);

            if (count == 0) throw new InvalidOperationException($"No matching outgoing envelope for {envelope}");

            await tx.CommitAsync(cancellationToken);
        }
        catch (SqlException e)
        {
            if (e.Number is 2627 or 2601)
            {
                await using var cleanupCmd = conn.CreateCommand(
                        $"delete from {_queue.Parent.MessageStorageSchemaName}.{DatabaseConstants.OutgoingTable} where id = @id")
                    .With("id", envelope.Id);
                await cleanupCmd.ExecuteNonQueryAsync(cancellationToken);

                return;
            }

            throw;
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    public async Task SendAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        try
        {
            if (envelope.IsScheduledForLater(DateTimeOffset.UtcNow))
            {
                await scheduleMessageAsync(envelope, cancellationToken, conn);
            }
            else
            {
                try
                {
                    await using var cmd = conn.CreateCommand(_writeDirectlyToQueueTableSql)
                        .With("id", envelope.Id)
                        .With("body", EnvelopeSerializer.Serialize(envelope))
                        .With("type", envelope.MessageType!)
                        .With("expires", envelope.DeliverBy!);
                    await cmd.ExecuteNonQueryAsync(cancellationToken);
                }
                catch (SqlException e)
                {
                    // Making this idempotent, but optimistically
                    if (e.Number is 2627 or 2601) return;
                    throw;
                }
            }
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    private async Task scheduleMessageAsync(Envelope envelope, CancellationToken cancellationToken,
        SqlConnection conn)
    {
        await using var cmd = conn.CreateCommand(_writeDirectlyToTheScheduledTable)
            .With("id", envelope.Id)
            .With("body", EnvelopeSerializer.Serialize(envelope))
            .With("type", envelope.MessageType!)
            .With("expires", envelope.DeliverBy!)
            .With("time", envelope.ScheduledTime!);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
