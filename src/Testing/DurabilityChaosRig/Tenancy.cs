using Npgsql;

namespace DurabilityChaosRig;

/// <summary>
/// CHAOS_TENANTS=1 (Marten store only): a database per tenant. Tenant "red" is a database on the main PostgreSQL
/// container, tenant "blue" a database on CHAOS_TENANT_CONTAINER (wolverine-tenant-pg on port 5445 by default), so
/// CHAOS_DB_CONTAINER=wolverine-tenant-pg makes a dbdown fault take one tenant's database away while the main
/// database (queues, nodes, the main inbox) and the other tenant keep working
/// </summary>
public static class Tenancy
{
    public static bool Enabled => Environment.GetEnvironmentVariable("CHAOS_TENANTS") == "1";

    private static int _next;

    public static string Next() => Interlocked.Increment(ref _next) % 2 == 0 ? "red" : "blue";

    private static string databaseName => Node.Schema + "_tenant";

    private static string redServer => Node.PostgresConnectionString;

    private static string blueServer =>
        new NpgsqlConnectionStringBuilder(Node.PostgresConnectionString)
        {
            Port = int.Parse(Environment.GetEnvironmentVariable("CHAOS_TENANT_PORT") ?? "5445")
        }.ConnectionString;

    private static string onDatabase(string server, string database) =>
        new NpgsqlConnectionStringBuilder(server) { Database = database }.ConnectionString;

    public static IEnumerable<(string tenant, string connectionString)> Databases =>
    [
        ("red", onDatabase(redServer, databaseName)),
        ("blue", onDatabase(blueServer, databaseName))
    ];

    /// <summary>Every database the driver checks: the main one, then each tenant's</summary>
    public static IEnumerable<string> AllConnectionStrings =>
        Enabled ? new[] { Db.ConnectionString }.Concat(Databases.Select(x => x.connectionString)) : [Db.ConnectionString];

    /// <summary>Drop and recreate each tenant database, so a previous run cannot leak into this one</summary>
    public static async Task ResetAsync()
    {
        if (!Enabled) return;

        foreach (var server in new[] { redServer, blueServer })
        {
            await using var conn = new NpgsqlConnection(server);
            await conn.OpenAsync();
            await using (var drop = conn.CreateCommand())
            {
                drop.CommandText = $"drop database if exists {databaseName} with (force)";
                await drop.ExecuteNonQueryAsync();
            }

            await using var create = conn.CreateCommand();
            create.CommandText = $"create database {databaseName}";
            await create.ExecuteNonQueryAsync();
        }
    }
}
