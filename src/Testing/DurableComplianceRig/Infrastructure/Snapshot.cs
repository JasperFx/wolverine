using System.Data.Common;

namespace DurableComplianceRig.Infrastructure;

public record InboxRow(string Database, Guid Id, string Status, int OwnerId, int Attempts, string? ReceivedAt,
    DateTimeOffset? ExecutionTime, string? MessageType);

public record DeadLetterRow(string Database, Guid Id, string? MessageType, string? ExceptionType, string? ReceivedAt);

public record OutgoingRow(string Database, Guid Id, string? Destination, int OwnerId);

public record LedgerRow(string Database, Guid MessageId, int Attempt, string Node, string? Tenant,
    DateTimeOffset StartedAt, DateTimeOffset RecordedAt);

/// <summary>Everything the invariant checker judges, read straight from every database a run touched.</summary>
public class RigSnapshot
{
    public List<InboxRow> Inbox { get; } = [];
    public List<DeadLetterRow> DeadLetters { get; } = [];
    public List<OutgoingRow> Outgoing { get; } = [];
    public List<LedgerRow> Ledger { get; } = [];
    public IReadOnlyList<Execution> Executions { get; init; } = [];
}

public enum Dialect
{
    Postgresql,
    SqlServer
}

/// <summary>
/// One database the rig reads: its Wolverine tables and the shared <c>rig_ledger</c> table, in one schema.
/// A per-tenant run has one of these per tenant database.
/// </summary>
public class RigDatabase(string label, Dialect dialect, Func<DbConnection> connect, string schema)
{
    public const string LedgerTable = "rig_ledger";

    public string Label { get; } = label;
    public Dialect Dialect { get; } = dialect;
    public string Schema { get; } = schema;

    public DbConnection Connect() => connect();

    public string CreateLedgerSql => Dialect == Dialect.Postgresql
        ? $"""
           create schema if not exists {Schema};
           create table if not exists {Schema}.{LedgerTable} (
               id bigserial primary key,
               message_id uuid not null,
               attempt int not null,
               node varchar(100) not null,
               tenant varchar(100) null,
               started_at timestamptz not null,
               recorded_at timestamptz not null);
           """
        : $"""
           if not exists (select 1 from sys.schemas where name = '{Schema}') exec('create schema {Schema}');
           if object_id('{Schema}.{LedgerTable}') is null
           create table {Schema}.{LedgerTable} (
               id bigint identity primary key,
               message_id uniqueidentifier not null,
               attempt int not null,
               node varchar(100) not null,
               tenant varchar(100) null,
               started_at datetimeoffset not null,
               recorded_at datetimeoffset not null);
           """;

    public string InsertLedgerSql =>
        $"insert into {Schema}.{LedgerTable} (message_id, attempt, node, tenant, started_at, recorded_at) values (@message_id, @attempt, @node, @tenant, @started_at, @recorded_at)";

    public async Task EnsureLedgerAsync()
    {
        await using var conn = Connect();
        await conn.OpenAsync();
        await executeAsync(conn, CreateLedgerSql);
    }

    public async Task ClearLedgerAsync()
    {
        await using var conn = Connect();
        await conn.OpenAsync();
        await executeAsync(conn, $"delete from {Schema}.{LedgerTable}");
    }

    public async Task ReadIntoAsync(RigSnapshot snapshot)
    {
        await using var conn = Connect();
        await conn.OpenAsync();

        await readAsync(conn,
            $"select id, status, owner_id, attempts, received_at, execution_time, message_type from {Schema}.wolverine_incoming_envelopes",
            r => snapshot.Inbox.Add(new InboxRow(Label, r.GetGuid(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3),
                r.IsDBNull(4) ? null : r.GetString(4),
                r.IsDBNull(5) ? null : readTime(r, 5),
                r.IsDBNull(6) ? null : r.GetString(6))));

        await readAsync(conn,
            $"select id, message_type, exception_type, received_at from {Schema}.wolverine_dead_letters",
            r => snapshot.DeadLetters.Add(new DeadLetterRow(Label, r.GetGuid(0),
                r.IsDBNull(1) ? null : r.GetString(1),
                r.IsDBNull(2) ? null : r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3))));

        await readAsync(conn,
            $"select id, destination, owner_id from {Schema}.wolverine_outgoing_envelopes",
            r => snapshot.Outgoing.Add(new OutgoingRow(Label, r.GetGuid(0), r.IsDBNull(1) ? null : r.GetString(1),
                r.GetInt32(2))));

        await readAsync(conn,
            $"select message_id, attempt, node, tenant, started_at, recorded_at from {Schema}.{LedgerTable}",
            r => snapshot.Ledger.Add(new LedgerRow(Label, r.GetGuid(0), r.GetInt32(1), r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3), readTime(r, 4), readTime(r, 5))));
    }

    private static DateTimeOffset readTime(DbDataReader reader, int ordinal)
    {
        var value = reader.GetValue(ordinal);
        return value switch
        {
            DateTimeOffset dto => dto,
            DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
            _ => throw new InvalidOperationException($"Unexpected time value {value} ({value.GetType()})")
        };
    }

    private static async Task executeAsync(DbConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task readAsync(DbConnection conn, string sql, Action<DbDataReader> read)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) read(reader);
    }
}
