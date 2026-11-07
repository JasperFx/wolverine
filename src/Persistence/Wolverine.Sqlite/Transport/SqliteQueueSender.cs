using System.Data.Common;
using JasperFx.Core;
using Microsoft.Data.Sqlite;
using Weasel.Core;
using Wolverine.Configuration;
using Wolverine.RDBMS;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports.Sending;

namespace Wolverine.Sqlite.Transport;

internal interface ISqliteQueueSender : ISender
{
    Task ScheduleRetryAsync(Envelope envelope, CancellationToken cancellationToken);
}

internal class SqliteQueueSender : ISqliteQueueSender
{
    // Preserve sub-second precision to keep queue promotion and envelope scheduling aligned
    private const string SqliteDateTimeFormat = "yyyy-MM-dd HH:mm:ss.fffffff";

    private readonly SqliteQueue _queue;
    private readonly DbDataSource _dataSource;

    private readonly string _moveFromOutgoingToQueueSql;
    private readonly string _moveFromOutgoingToScheduledSql;
    private readonly string _deleteFromIncomingSql;
    private readonly string _writeDirectlyToQueueTableSql;
    private readonly string _writeDirectlyToTheScheduledTable;
    private readonly string _outgoingTable;

    public SqliteQueueSender(SqliteQueue queue) : this(queue, queue.DataSource, null)
    {
        Destination = queue.Uri;
    }

    public SqliteQueueSender(SqliteQueue queue, DbDataSource dataSource, string? databaseName)
    {
        _queue = queue;
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

        Destination = SqliteQueue.ToUri(queue.Name, databaseName);

        // GH-3943: the durability tables this sender drains carry the message store's schema name as
        // a prefix on SQLite. Queue table names are resolved by the queue from the transport's
        // optional prefix. A null Store means there are no prefixed durability tables to drain.
        _outgoingTable = queue.Parent.Store?.TableNameFor(DatabaseConstants.OutgoingTable)
                            ?? DatabaseConstants.OutgoingTable;
        var incomingTable = queue.Parent.Store?.TableNameFor(DatabaseConstants.IncomingTable)
                            ?? DatabaseConstants.IncomingTable;

        _moveFromOutgoingToQueueSql = $@"
INSERT into {queue.QueueTable.Identifier} ({DatabaseConstants.Id}, {DatabaseConstants.Body}, {DatabaseConstants.MessageType}, {DatabaseConstants.KeepUntil})
SELECT {DatabaseConstants.Id}, {DatabaseConstants.Body}, {DatabaseConstants.MessageType}, {DatabaseConstants.DeliverBy}
FROM
    {_outgoingTable}
WHERE lower({DatabaseConstants.Id}) = lower(@id);
DELETE FROM {_outgoingTable} WHERE lower({DatabaseConstants.Id}) = lower(@id);
";

        _moveFromOutgoingToScheduledSql = $@"
INSERT into {queue.ScheduledTable.Identifier} ({DatabaseConstants.Id}, {DatabaseConstants.Body}, {DatabaseConstants.MessageType}, {DatabaseConstants.ExecutionTime})
SELECT {DatabaseConstants.Id}, {DatabaseConstants.Body}, {DatabaseConstants.MessageType}, @time
FROM
    {_outgoingTable}
WHERE lower({DatabaseConstants.Id}) = lower(@id);
DELETE FROM {_outgoingTable} WHERE lower({DatabaseConstants.Id}) = lower(@id);
";

        _writeDirectlyToQueueTableSql =
            $@"insert or replace into {queue.QueueTable.Identifier} ({DatabaseConstants.Id}, {DatabaseConstants.Body}, {DatabaseConstants.MessageType}, {DatabaseConstants.KeepUntil}) values (@id, @body, @type, @expires)";

        _writeDirectlyToTheScheduledTable = $@"
insert or replace into {queue.ScheduledTable.Identifier} ({DatabaseConstants.Id}, {DatabaseConstants.Body}, {DatabaseConstants.MessageType}, {DatabaseConstants.ExecutionTime})
values (@id, @body, @type, @time)
".Trim();

        _deleteFromIncomingSql = $"DELETE FROM {incomingTable} WHERE lower(id) = lower(@id)";
    }

    public bool SupportsNativeScheduledSend => true;
    public Uri Destination { get; private set; }

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

    public async Task ScheduleMessageAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await writeToScheduledTableAsync(envelope, cancellationToken, conn);
    }

    private async Task writeToScheduledTableAsync(Envelope envelope, CancellationToken cancellationToken, DbConnection conn,
        DbTransaction? tx = null)
    {
        var scheduledTime = envelope.ScheduledTime ?? DateTimeOffset.UtcNow;
        var cmd = conn.CreateCommand(_writeDirectlyToTheScheduledTable)
            .With("id", $"{envelope.Id:D}")
            .With("body", EnvelopeSerializer.Serialize(envelope))
            .With("type", envelope.MessageType!)
            .With("time", scheduledTime.UtcDateTime.ToString(SqliteDateTimeFormat));
        cmd.Transaction = tx;
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async ValueTask SendAsync(Envelope envelope, CancellationToken cancellation)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellation).ConfigureAwait(false);

        if (envelope.IsScheduledForLater(DateTimeOffset.UtcNow))
        {
            await writeToScheduledTableAsync(envelope, cancellation, conn);
        }
        else
        {
            await sendAsync(envelope, conn, cancellation);
        }
    }

    private async Task sendAsync(Envelope envelope, DbConnection conn, CancellationToken cancellation,
        DbTransaction? tx = null)
    {
        var cmd = conn.CreateCommand(_writeDirectlyToQueueTableSql)
            .With("id", $"{envelope.Id:D}")
            .With("body", EnvelopeSerializer.Serialize(envelope))
            .With("type", envelope.MessageType!)
            .With("expires", (envelope.DeliverBy?.UtcDateTime.ToString(SqliteDateTimeFormat))!);
        cmd.Transaction = tx;
        await cmd.ExecuteNonQueryAsync(cancellation);
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
        var owner = _queue.Parent.Store?.Durability.AssignedNodeNumber;

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        // The inbox delete and the scheduled write commit together or not at all: autocommitted one by one, a
        // failed scheduled write left the inbox row deleted with nothing scheduled
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);

        var delete = _deleteFromIncomingSql;
        if (owner.HasValue) delete += $" AND {DatabaseConstants.OwnerId} = @owner";

        await using (var deleteCmd = conn.CreateCommand(delete).With("id", $"{envelope.Id:D}"))
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

        await writeToScheduledTableAsync(envelope, cancellationToken, conn, tx);
        await tx.CommitAsync(cancellationToken);
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
    ///     one, in a single transaction. The envelope's outbox row may exist even though WasPersistedInOutbox is
    ///     false: the EF Core outbox writes it through its own unit of work, and outbox recovery reads it back
    ///     without the flag. Doing both in one transaction means a node dying in between can't leave a row
    ///     behind for recovery to send again
    /// </summary>
    public async Task MoveOrWriteToQueueAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);

        if (envelope.IsScheduledForLater(DateTimeOffset.UtcNow))
        {
            await writeToScheduledTableAsync(envelope, cancellationToken, conn, tx);
        }
        else
        {
            await sendAsync(envelope, conn, cancellationToken, tx);
        }

        var delete = conn.CreateCommand($"DELETE FROM {_outgoingTable} WHERE lower({DatabaseConstants.Id}) = lower(@id)")
            .With("id", $"{envelope.Id:D}");
        delete.Transaction = tx;
        await delete.ExecuteNonQueryAsync(cancellationToken);

        await tx.CommitAsync(cancellationToken);
    }

    public async Task MoveFromOutgoingToQueueAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var count = await conn.CreateCommand(_moveFromOutgoingToQueueSql)
                .With("id", $"{envelope.Id:D}")
                .ExecuteNonQueryAsync(cancellationToken);

            if (count == 0) throw new InvalidOperationException("No matching outgoing envelope");
        }
        catch (SqliteException e)
        {
            if (e.Message.ContainsIgnoreCase("UNIQUE constraint failed")) return;
            throw;
        }
    }

    public async Task MoveFromOutgoingToScheduledAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        if (!envelope.ScheduledTime.HasValue)
            throw new InvalidOperationException("This envelope has no scheduled time");

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var count = await conn.CreateCommand(_moveFromOutgoingToScheduledSql)
                .With("id", $"{envelope.Id:D}")
                .With("time", envelope.ScheduledTime!.Value.UtcDateTime.ToString(SqliteDateTimeFormat))
                .ExecuteNonQueryAsync(cancellationToken);

            if (count == 0) throw new InvalidOperationException($"No matching outgoing envelope for {envelope}");
        }
        catch (SqliteException e)
        {
            if (e.Message.ContainsIgnoreCase("UNIQUE constraint failed"))
            {
                await conn.CreateCommand($"DELETE FROM {_outgoingTable} WHERE lower(id) = lower(@id)")
                    .With("id", $"{envelope.Id:D}")
                    .ExecuteNonQueryAsync(cancellationToken);

                return;
            }

            throw;
        }
    }
}
