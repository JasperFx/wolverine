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

    public NServiceBusPostgresqlQueueSender(NServiceBusPostgresqlQueue queue, IWolverineRuntime runtime)
    {
        _queue = queue;
        _dataSource = queue.Parent.Store.NpgsqlDataSource;
        _mapper = queue.BuildMapper(runtime);
        Destination = queue.Uri;

        // The documented NServiceBus PostgreSQL send statement. The column identifiers are left
        // unquoted: Weasel provisions the queue table with case-folded (lowercase) column names,
        // so unquoted references resolve correctly.
        _sendSql =
            $@"INSERT INTO {queue.TableIdentifier} (Id, Expires, Headers, Body) VALUES (:id, :expires, :headers, :body)";
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
            var cmd = conn.CreateCommand(_sendSql)
                .With("id", row.Id)
                .With("headers", row.Headers)
                .With("body", row.Body);

            // The NServiceBus Expires column is a plain timestamp; bind it with an explicit
            // type so a null is sent as a typed NULL rather than an untyped parameter.
            cmd.With("expires", (object?)row.Expires?.UtcDateTime ?? DBNull.Value, DbType.DateTime);

            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            await conn.CloseAsync();
        }
    }
}
