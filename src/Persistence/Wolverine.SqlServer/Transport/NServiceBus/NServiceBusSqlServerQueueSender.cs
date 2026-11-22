using System.Data;
using Microsoft.Data.SqlClient;
using Weasel.SqlServer;
using Wolverine.Runtime;
using Wolverine.Transports.Sending;

namespace Wolverine.SqlServer.Transport.NServiceBus;

internal class NServiceBusSqlServerQueueSender : ISender
{
    private readonly NServiceBusSqlServerQueue _queue;
    private readonly string _connectionString;
    private readonly NServiceBusSqlServerEnvelopeMapper _mapper;
    private readonly string _sendSql;
    private readonly string _guardedReinsertSql;

    public NServiceBusSqlServerQueueSender(NServiceBusSqlServerQueue queue, IWolverineRuntime runtime)
    {
        _queue = queue;
        _connectionString = queue.Parent.Settings.ConnectionString!;
        _mapper = queue.BuildMapper(runtime);
        Destination = queue.Uri;

        // The documented NServiceBus SQL Server send statement.
        _sendSql =
            $@"INSERT INTO {queue.TableIdentifier} (Id, Recoverable, Expires, Headers, Body) VALUES (@Id, 1, @Expires, @Headers, @Body)";

        // GH-5775. A retried re-insert of a popped message may follow an attempt that committed while
        // reporting a failure. NServiceBus puts no constraint on Id (the table is clustered on RowVersion), so
        // the guard is an explicit check. It scans, since Id is not indexed, which is why only a retry uses it.
        _guardedReinsertSql =
            $@"IF NOT EXISTS (SELECT 1 FROM {queue.TableIdentifier} WHERE Id = @Id) {_sendSql}";
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

    public ValueTask SendAsync(Envelope envelope)
    {
        return new ValueTask(insertAsync(envelope, _sendSql));
    }

    /// <summary>
    /// GH-5775. Put a message the listener popped back into the queue table. A <paramref name="retry"/> inserts
    /// only when no row with the message's id is there yet.
    /// </summary>
    internal Task ReinsertAsync(Envelope envelope, bool retry)
    {
        return insertAsync(envelope, retry ? _guardedReinsertSql : _sendSql);
    }

    private async Task insertAsync(Envelope envelope, string sql)
    {
        var row = _mapper.MapOutgoing(envelope);

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        try
        {
            var cmd = conn.CreateCommand(sql)
                .With("Id", row.Id)
                .With("Headers", row.Headers)
                .With("Body", row.Body);

            // The NServiceBus Expires column is a plain datetime; bind it with an explicit
            // type so a null doesn't get inferred as sql_variant (which datetime rejects).
            cmd.Parameters.Add(new SqlParameter("Expires", SqlDbType.DateTime)
            {
                Value = (object?)row.Expires?.UtcDateTime ?? DBNull.Value
            });

            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            await conn.CloseAsync();
        }
    }
}
