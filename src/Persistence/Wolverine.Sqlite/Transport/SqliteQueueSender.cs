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
    private readonly string _deleteFromIncomingAndScheduleSql;
    private readonly string _writeDirectlyToQueueTableSql;
    private readonly string _writeDirectlyToTheScheduledTable;

    public SqliteQueueSender(SqliteQueue queue) : this(queue, queue.DataSource, null)
    {
        Destination = queue.Uri;
    }

    public SqliteQueueSender(SqliteQueue queue, DbDataSource dataSource, string? databaseName)
    {
        _queue = queue;
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

        Destination = SqliteQueue.ToUri(queue.Name, databaseName);

        _moveFromOutgoingToQueueSql = $@"
INSERT into {queue.QueueTable.Identifier} ({DatabaseConstants.Id}, {DatabaseConstants.Body}, {DatabaseConstants.MessageType}, {DatabaseConstants.KeepUntil})
SELECT {DatabaseConstants.Id}, {DatabaseConstants.Body}, {DatabaseConstants.MessageType}, {DatabaseConstants.DeliverBy}
FROM
    {DatabaseConstants.OutgoingTable}
WHERE lower({DatabaseConstants.Id}) = lower(@id);
DELETE FROM {DatabaseConstants.OutgoingTable} WHERE lower({DatabaseConstants.Id}) = lower(@id);
";

        _moveFromOutgoingToScheduledSql = $@"
INSERT into {queue.ScheduledTable.Identifier} ({DatabaseConstants.Id}, {DatabaseConstants.Body}, {DatabaseConstants.MessageType}, {DatabaseConstants.ExecutionTime})
SELECT {DatabaseConstants.Id}, {DatabaseConstants.Body}, {DatabaseConstants.MessageType}, @time
FROM
    {DatabaseConstants.OutgoingTable}
WHERE lower({DatabaseConstants.Id}) = lower(@id);
DELETE FROM {DatabaseConstants.OutgoingTable} WHERE lower({DatabaseConstants.Id}) = lower(@id);
";

        _writeDirectlyToQueueTableSql =
            $@"insert or replace into {queue.QueueTable.Identifier} ({DatabaseConstants.Id}, {DatabaseConstants.Body}, {DatabaseConstants.MessageType}, {DatabaseConstants.KeepUntil}) values (@id, @body, @type, @expires)";

        _writeDirectlyToTheScheduledTable = $@"
insert or replace into {queue.ScheduledTable.Identifier} ({DatabaseConstants.Id}, {DatabaseConstants.Body}, {DatabaseConstants.MessageType}, {DatabaseConstants.ExecutionTime})
values (@id, @body, @type, @time)
".Trim();

        _deleteFromIncomingAndScheduleSql =
            $"DELETE FROM {DatabaseConstants.IncomingTable} WHERE lower(id) = lower(@id);" + _writeDirectlyToTheScheduledTable;
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

    private async Task writeToScheduledTableAsync(Envelope envelope, CancellationToken cancellationToken, DbConnection conn)
    {
        var scheduledTime = envelope.ScheduledTime ?? DateTimeOffset.UtcNow;
        await conn.CreateCommand(_writeDirectlyToTheScheduledTable)
            .With("id", $"{envelope.Id:D}")
            .With("body", EnvelopeSerializer.Serialize(envelope))
            .With("type", envelope.MessageType)
            .With("time", scheduledTime.UtcDateTime.ToString(SqliteDateTimeFormat))
            .ExecuteNonQueryAsync(cancellationToken);
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

    private async Task sendAsync(Envelope envelope, DbConnection conn, CancellationToken cancellation)
    {
        await conn.CreateCommand(_writeDirectlyToQueueTableSql)
            .With("id", $"{envelope.Id:D}")
            .With("body", EnvelopeSerializer.Serialize(envelope))
            .With("type", envelope.MessageType)
            .With("expires", envelope.DeliverBy?.UtcDateTime.ToString(SqliteDateTimeFormat))
            .ExecuteNonQueryAsync(cancellation);
    }

    public async Task ScheduleRetryAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await conn.CreateCommand(_deleteFromIncomingAndScheduleSql)
            .With("id", $"{envelope.Id:D}")
            .With("body", EnvelopeSerializer.Serialize(envelope))
            .With("type", envelope.MessageType)
            .With("time", (envelope.ScheduledTime ?? DateTimeOffset.UtcNow).UtcDateTime.ToString(SqliteDateTimeFormat))
            .ExecuteNonQueryAsync(cancellationToken);
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
        else
        {
            await SendAsync(envelope, CancellationToken.None);
        }
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
                await conn.CreateCommand($"DELETE FROM {DatabaseConstants.OutgoingTable} WHERE lower(id) = lower(@id)")
                    .With("id", $"{envelope.Id:D}")
                    .ExecuteNonQueryAsync(cancellationToken);

                return;
            }

            throw;
        }
    }
}
