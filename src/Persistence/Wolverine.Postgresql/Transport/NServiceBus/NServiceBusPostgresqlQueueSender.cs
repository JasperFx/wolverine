using System.Data;
using Npgsql;
using Weasel.Core;
using Weasel.Postgresql;
using Wolverine.Runtime;
using Wolverine.Transports.Sending;

namespace Wolverine.Postgresql.Transport.NServiceBus;

internal class NServiceBusPostgresqlQueueSender : ISender
{
    private readonly NServiceBusPostgresqlQueue _queue;
    private readonly NpgsqlDataSource _dataSource;
    private readonly NServiceBusPostgresqlEnvelopeMapper _mapper;
    private readonly string _sendSql;
    private readonly string _reinsertSql;
    private readonly string _reinsertWithSeqSql;
    private readonly string _existsSql;

    public NServiceBusPostgresqlQueueSender(NServiceBusPostgresqlQueue queue, IWolverineRuntime runtime)
    {
        _queue = queue;
        _dataSource = queue.Parent.Store.NpgsqlDataSource;
        _mapper = queue.BuildMapper(runtime);
        Destination = queue.Uri;

        // The documented NServiceBus PostgreSQL send statement. The column identifiers are left
        // unquoted, which PostgreSQL folds to lowercase — matching the lowercase columns both
        // NServiceBus and NServiceBusQueueTable declare.
        _sendSql =
            $@"INSERT INTO {queue.TableIdentifier} (id, expires, headers, body) VALUES (:id, :expires, :headers, :body)";

        // GH-5775. Putting a popped message back. ON CONFLICT DO NOTHING makes a retry after an attempt that
        // committed while reporting a failure a no-op instead of a primary key violation on id.
        _reinsertSql = _sendSql + " ON CONFLICT DO NOTHING";
        _reinsertWithSeqSql =
            $@"INSERT INTO {queue.TableIdentifier} (id, expires, headers, body, seq) VALUES (:id, :expires, :headers, :body, :seq) ON CONFLICT DO NOTHING";
        _existsSql = $"SELECT EXISTS (SELECT 1 FROM {queue.TableIdentifier} WHERE id = :id)";
    }

    // NServiceBus delayed delivery uses a separate timeout table + mover; not supported in v1.
    public bool SupportsNativeScheduledSend => false;

    public Uri Destination { get; }

    public async Task<bool> PingAsync()
    {
        try
        {
            return await _queue.ExistsAsync();
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async ValueTask SendAsync(Envelope envelope)
    {
        var row = _mapper.MapOutgoing(envelope);

        await using var conn = await _dataSource.OpenConnectionAsync();
        try
        {
            await buildInsert(conn, _sendSql, row).ExecuteNonQueryAsync();
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    /// <summary>
    /// GH-5775. Put a message the listener popped back into the queue table. With <paramref name="seq"/> the row
    /// gets its original seq back and so its FIFO place, which is where a rolled back NServiceBus receive leaves
    /// it. Safe to repeat: a copy that is already there (the id is the primary key) is left alone.
    /// </summary>
    internal async Task ReinsertAsync(Envelope envelope, long? seq, CancellationToken token)
    {
        // GH-5830. The copy carries the attempts the message has had, so attempt-bounded error policies still count
        var row = _mapper.MapOutgoing(envelope, includeAttempts: true);

        await using var conn = await _dataSource.OpenConnectionAsync(token);
        try
        {
            if (seq.HasValue)
            {
                var inserted = await buildInsert(conn, _reinsertWithSeqSql, row)
                    .With("seq", seq.Value)
                    .ExecuteNonQueryAsync(token);

                if (inserted > 0) return;

                // Nothing inserted: either the copy is already there, or (not expected, the seq was this row's)
                // something else holds the seq. Only the first is done.
                var exists = (bool)(await conn.CreateCommand(_existsSql).With("id", row.Id)
                    .ExecuteScalarAsync(token))!;
                if (exists) return;
            }

            await buildInsert(conn, _reinsertSql, row).ExecuteNonQueryAsync(token);
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    private static NpgsqlCommand buildInsert(NpgsqlConnection conn, string sql, NServiceBusPostgresqlEnvelopeMapper.OutgoingRow row)
    {
        var cmd = conn.CreateCommand(sql)
            .With("id", row.Id)
            .With("headers", row.Headers)
            .With("body", row.Body);

        // The NServiceBus Expires column is a plain timestamp; bind it with an explicit
        // type so a null is sent as a typed NULL rather than an untyped parameter.
        cmd.With("expires", (object?)row.Expires?.UtcDateTime ?? DBNull.Value, DbType.DateTime);
        return cmd;
    }
}
