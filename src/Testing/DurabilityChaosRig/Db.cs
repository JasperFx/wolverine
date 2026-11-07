using System.Data.Common;
using IntegrationTests;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using MySqlConnector;
using Npgsql;
using Oracle.ManagedDataAccess.Client;
using Weasel.Postgresql;
using Weasel.SqlServer;

namespace DurabilityChaosRig;

public enum DbKind
{
    Postgres,
    SqlServer,
    MySql,
    Oracle,
    Sqlite
}

/// <summary>
/// Everything the driver and the nodes need to know about the database a run uses: how to connect, where the
/// Wolverine and ledger tables are, and the few places the SQL differs between databases
/// </summary>
public static class Db
{
    public static DbKind Kind => Node.Store switch
    {
        "ef" => DbKind.SqlServer,
        "mysql" => DbKind.MySql,
        "oracle" => DbKind.Oracle,
        "sqlite" => DbKind.Sqlite,
        _ => DbKind.Postgres // marten, efpg
    };

    /// <summary>The ledger is Marten documents for marten, and plain sent / processed tables for every other store</summary>
    public static bool MartenLedger => Node.Store == "marten";

    /// <summary>Oracle keeps everything in the WOLVERINE user's schema, whatever CHAOS_SCHEMA says</summary>
    public static string Schema => Kind == DbKind.Oracle ? "WOLVERINE" : Node.Schema;

    public static string SqliteFile => Path.Combine(Node.Dir, "chaos.db");

    /// <summary>The connection string the nodes use. With CHAOS_TOXIPROXY=1 PostgreSQL goes through the proxy</summary>
    public static string NodeConnectionString => Kind == DbKind.Postgres && Toxiproxy.Enabled
        ? Toxiproxy.PostgresConnectionString
        : ConnectionString;

    /// <summary>The connection string the driver uses, never through the proxy</summary>
    public static string ConnectionString => Kind switch
    {
        DbKind.SqlServer => Node.SqlServerConnectionString,
        DbKind.MySql => shortTimeouts(Servers.MySqlConnectionString, "Default Command Timeout=5"),
        DbKind.Oracle => Servers.OracleConnectionString,
        DbKind.Sqlite => $"Data Source={SqliteFile}",
        _ => Node.PostgresConnectionString
    };

    private static string shortTimeouts(string connectionString, string setting) =>
        Environment.GetEnvironmentVariable("CHAOS_SHORT_TIMEOUTS") == "1"
            ? connectionString.TrimEnd(';') + ";" + setting
            : connectionString;

    /// <summary>The database container a dbdown fault pauses, or null when there is none to pause</summary>
    public static string? Container => Kind switch
    {
        DbKind.SqlServer => Environment.GetEnvironmentVariable("CHAOS_DB_CONTAINER") ?? "wolverine-sqlserver-1",
        DbKind.MySql => Environment.GetEnvironmentVariable("CHAOS_DB_CONTAINER") ?? "wolverine-mysql-1",
        DbKind.Oracle => Environment.GetEnvironmentVariable("CHAOS_DB_CONTAINER") ?? "wolverine-oracle-1",
        DbKind.Sqlite => null,
        _ => Environment.GetEnvironmentVariable("CHAOS_DB_CONTAINER") ?? "wolverine-postgresql-1"
    };

    public static DbConnection Open(string? connectionString = null)
    {
        connectionString ??= ConnectionString;
        return Kind switch
        {
            DbKind.SqlServer => new SqlConnection(connectionString),
            DbKind.MySql => new MySqlConnection(connectionString),
            DbKind.Oracle => new OracleConnection(connectionString),
            DbKind.Sqlite => new SqliteConnection(connectionString),
            _ => new NpgsqlConnection(connectionString)
        };
    }

    public static string Table(string name) => Kind == DbKind.Sqlite ? name : $"{Schema}.{name}";

    public static string Incoming => Table("wolverine_incoming_envelopes");
    public static string Outgoing => Table("wolverine_outgoing_envelopes");
    public static string DeadLetters => Table("wolverine_dead_letters");

    public static string SentTable => MartenLedger ? Table("mt_doc_sent") : Table(Kind == DbKind.Oracle ? "chaos_sent" : "sent");

    public static string ProcessedTable =>
        MartenLedger ? Table("mt_doc_processed") : Table(Kind == DbKind.Oracle ? "chaos_processed" : "processed");

    public static string True => Kind == DbKind.Postgres ? "true" : "1";
    public static string False => Kind == DbKind.Postgres ? "false" : "0";

    public static string BitAnd(string left, int right) =>
        Kind == DbKind.Oracle ? $"bitand({left}, {right})" : $"({left} & {right})";

    public static string StringAgg(string expression) => Kind switch
    {
        DbKind.MySql => $"group_concat({expression})",
        DbKind.Sqlite => $"group_concat({expression}, ',')",
        DbKind.Oracle => $"listagg({expression}, ',') within group (order by 1)",
        _ => $"string_agg({expression}, ',')"
    };

    public static string Concat(params string[] parts) => Kind is DbKind.SqlServer or DbKind.MySql
        ? $"concat({string.Join(", ", parts)})"
        : string.Join(" || ", parts);

    /// <summary>Seconds between two timestamp columns</summary>
    public static string Seconds(string from, string to) => Kind switch
    {
        DbKind.SqlServer => $"datediff_big(millisecond, {from}, {to}) / 1000.0",
        DbKind.MySql => $"timestampdiff(microsecond, {from}, {to}) / 1000000.0",
        DbKind.Sqlite => $"(julianday({to}) - julianday({from})) * 86400.0",
        DbKind.Oracle => $"(extract(day from ({to} - {from})) * 86400 + extract(hour from ({to} - {from})) * 3600 + " +
                         $"extract(minute from ({to} - {from})) * 60 + extract(second from ({to} - {from})))",
        _ => $"extract(epoch from ({to} - {from}))"
    };

    /// <summary>Whether a failed query only means a table that has not been created yet</summary>
    public static bool IsMissingTable(DbException e) =>
        e.Message.Contains("does not exist") || e.Message.Contains("Invalid object name") ||
        e.Message.Contains("doesn't exist") || e.Message.Contains("ORA-00942") || e.Message.Contains("no such table");

    /// <summary>Drop what the previous run left, so it cannot leak into this one</summary>
    public static async Task ResetAsync()
    {
        switch (Kind)
        {
            case DbKind.Sqlite:
                Directory.CreateDirectory(Node.Dir);
                foreach (var file in new[] { SqliteFile, SqliteFile + "-wal", SqliteFile + "-shm" }) File.Delete(file);
                break;

            case DbKind.MySql:
                await executeAsync(ConnectionString,
                    $"drop database if exists {Schema}; create database {Schema}; drop database if exists {Schema}_queues; create database {Schema}_queues;");
                break;

            case DbKind.Oracle:
                await using (var conn = Open())
                {
                    await conn.OpenAsync();
                    var tables = new List<string>();
                    await using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText =
                            "select table_name from user_tables where table_name like 'WOLVERINE%' or table_name like 'CHAOS%'";
                        await using var reader = await cmd.ExecuteReaderAsync();
                        while (await reader.ReadAsync()) tables.Add(reader.GetString(0));
                    }

                    foreach (var table in tables)
                    {
                        await using var drop = conn.CreateCommand();
                        drop.CommandText = $"drop table {table} cascade constraints purge";
                        await drop.ExecuteNonQueryAsync();
                    }
                }

                break;

            default:
                // PostgreSQL and SQL Server through Weasel, as before
                await SchemaReset.ResetAsync();
                await Tenancy.ResetAsync();
                break;
        }

        await createLedgerTablesAsync();
    }

    /// <summary>The ADO.NET stores have no ORM to create the ledger, so the driver does</summary>
    private static async Task createLedgerTablesAsync()
    {
        var (id, text, time) = Kind switch
        {
            DbKind.MySql => ("char(36)", "varchar(20)", "datetime(6)"),
            DbKind.Oracle => ("varchar2(36)", "varchar2(20)", "timestamp(6) with time zone"),
            DbKind.Sqlite => ("text", "text", "text"),
            _ => ((string?)null, (string?)null, (string?)null)
        };

        if (id == null) return;

        await executeAsync(ConnectionString,
            $"create table {SentTable} (id {id} primary key, flags int not null, at {time} not null)");
        await executeAsync(ConnectionString,
            $"create table {ProcessedTable} (id {id} primary key, message_id {id} not null, stage {text} not null, " +
            $"node {text} not null, attempts int not null, at {time} not null, envelope_id {id})");
    }

    private static async Task executeAsync(string connectionString, string sql)
    {
        await using var conn = Open(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>A timestamp parameter value the ADO.NET ledger stores compare correctly</summary>
    public static object Timestamp(DateTimeOffset value) => Kind switch
    {
        DbKind.Sqlite => value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.ffffff"),
        DbKind.MySql => value.UtcDateTime,
        _ => value
    };

    /// <summary>Ids are stored as text in the ADO.NET ledger tables</summary>
    public static object Id(Guid value) => value.ToString("D");
}

/// <summary>The PostgreSQL and SQL Server reset, kept as it was</summary>
internal static class SchemaReset
{
    public static async Task ResetAsync()
    {
        foreach (var schema in new[] { Node.Schema, Node.Schema + "_queues" })
        {
            if (Db.Kind == DbKind.SqlServer)
            {
                await using var conn = new SqlConnection(Node.SqlServerConnectionString);
                await conn.OpenAsync();
                await conn.DropSchemaAsync(schema);
            }
            else
            {
                await using var conn = new NpgsqlConnection(Node.PostgresConnectionString);
                await conn.OpenAsync();
                await conn.DropSchemaAsync(schema);
            }
        }
    }
}
