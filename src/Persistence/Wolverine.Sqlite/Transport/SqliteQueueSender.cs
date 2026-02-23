using System.Data.Common;
using JasperFx.Core;
using Weasel.Core;
using Wolverine.Configuration;
using Wolverine.RDBMS;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports.Sending;

namespace Wolverine.Sqlite.Transport;

internal interface ISqliteQueueSender : ISenderWithScheduledCancellation
{
    Task ScheduleRetryAsync(Envelope envelope, CancellationToken cancellationToken);
}

internal class SqliteQueueSender : ISqliteQueueSender
{
    // SQLite datetime format compatible with datetime('now') for reliable comparisons
    private const string SqliteDateTimeFormat = "yyyy-MM-dd HH:mm:ss";

    private readonly SqliteQueue _queue;
    private readonly DbDataSource _dataSource;

    private readonly string _moveFromOutgoingToQueueSql;
    private readonly string _moveFromOutgoingToScheduledSql;
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
WHERE {DatabaseConstants.Id} = @id;
DELETE FROM {DatabaseConstants.OutgoingTable} WHERE {DatabaseConstants.Id} = @id;
";

        _moveFromOutgoingToScheduledSql = $@"
INSERT into {queue.ScheduledTable.Identifier} ({DatabaseConstants.Id}, {DatabaseConstants.Body}, {DatabaseConstants.MessageType}, {DatabaseConstants.ExecutionTime})
SELECT {DatabaseConstants.Id}, {DatabaseConstants.Body}, {DatabaseConstants.MessageType}, @time
FROM
    {DatabaseConstants.OutgoingTable}
WHERE {DatabaseConstants.Id} = @id;
DELETE FROM {DatabaseConstants.OutgoingTable} WHERE {DatabaseConstants.Id} = @id;
";

        _writeDirectlyToQueueTableSql =
            $@"insert or replace into {queue.QueueTable.Identifier} ({DatabaseConstants.Id}, {DatabaseConstants.Body}, {DatabaseConstants.MessageType}, {DatabaseConstants.KeepUntil}) values (@id, @body, @type, @expires)";

        _writeDirectlyToTheScheduledTable = $@"
insert or replace into {queue.ScheduledTable.Identifier} ({DatabaseConstants.Id}, {DatabaseConstants.Body}, {DatabaseConstants.MessageType}, {DatabaseConstants.ExecutionTime})
values (@id, @body, @type, @time)
".Trim();
    }

    public bool SupportsNativeScheduledSend => true;
    public bool SupportsNativeScheduledCancellation => true;
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
            .With("id", envelope.Id.ToString())
            .With("body", EnvelopeSerializer.Serialize(envelope))
            .With("type", envelope.MessageType)
            .With("time", scheduledTime.UtcDateTime.ToString(SqliteDateTimeFormat))
            .ExecuteNonQueryAsync(cancellationToken);

        envelope.SchedulingToken = envelope.Id;
    }

    public ValueTask SendAsync(Envelope envelope)
    {
        return SendAsync(envelope, CancellationToken.None);
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
            .With("id", envelope.Id.ToString())
            .With("body", EnvelopeSerializer.Serialize(envelope))
            .With("type", envelope.MessageType)
            .With("expires", envelope.DeliverBy?.UtcDateTime.ToString(SqliteDateTimeFormat))
            .ExecuteNonQueryAsync(cancellation);
    }

    public async Task ScheduleRetryAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await writeToScheduledTableAsync(envelope, cancellationToken, conn);
    }

    public async Task CancelScheduledMessageAsync(object schedulingToken, CancellationToken cancellation = default)
    {
        if (schedulingToken is not Guid envelopeId)
        {
            throw new ArgumentException(
                $"Expected scheduling token of type Guid for SQLite queue sender, got {schedulingToken?.GetType().Name ?? "null"}",
                nameof(schedulingToken));
        }

        await using var conn = await _dataSource.OpenConnectionAsync(cancellation).ConfigureAwait(false);
        await conn.CreateCommand($"DELETE FROM {_queue.ScheduledTable.Identifier} WHERE id = @id")
            .With("id", envelopeId.ToString())
            .ExecuteNonQueryAsync(cancellation);
    }
}
