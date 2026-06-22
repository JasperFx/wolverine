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

    public NServiceBusSqlServerQueueSender(NServiceBusSqlServerQueue queue, IWolverineRuntime runtime)
    {
        _queue = queue;
        _connectionString = queue.Parent.Settings.ConnectionString!;
        _mapper = queue.BuildMapper(runtime);
        Destination = queue.Uri;

        // The documented NServiceBus SQL Server send statement.
        _sendSql =
            $@"INSERT INTO {queue.TableIdentifier} (Id, Recoverable, Expires, Headers, Body) VALUES (@Id, 1, @Expires, @Headers, @Body)";
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

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        try
        {
            var cmd = conn.CreateCommand(_sendSql)
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
