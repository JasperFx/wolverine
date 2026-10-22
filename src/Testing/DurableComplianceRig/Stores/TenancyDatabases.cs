using DurableComplianceRig.Infrastructure;
using IntegrationTests;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace DurableComplianceRig.Stores;

/// <summary>
/// The databases a per-tenant profile owns: one main database and one per tenant, all created by the rig. Every
/// name carries the profile key and <see cref="RigEnvironment.Suffix" />, so a run never touches a database another
/// run (or another test suite) uses, and the main database is the profile's own rather than <c>postgres</c> or
/// <c>master</c>.
/// </summary>
public abstract class TenancyDatabases(string key)
{
    public static readonly string[] Tenants = ["red", "blue", "green"];

    public const string MainLabel = "main";

    private readonly object _lock = new();
    private bool _created;

    public string NameFor(string label) =>
        $"rig_{key}_{label}" + (RigEnvironment.Suffix.Length == 0 ? "" : "_" + RigEnvironment.Suffix);

    public abstract Dialect Dialect { get; }

    public abstract string ConnectionStringFor(string label);

    /// <summary>
    /// The rig's own connections (snapshots, ledger setup) carry another application name, so they pool apart from
    /// Wolverine's: clearing them after an outage must not speed up Wolverine's reconnect.
    /// </summary>
    public abstract string RigConnectionStringFor(string label);

    /// <summary>
    /// Create any missing database and bring back any a crashed run left offline. Blocking, because host
    /// configuration is synchronous and the tenant connection strings have to exist by then; once per process.
    /// </summary>
    public void EnsureCreated()
    {
        lock (_lock)
        {
            if (_created) return;
            foreach (var label in Tenants.Prepend(MainLabel))
            {
                ensureCreated(NameFor(label));
            }

            _created = true;
        }
    }

    protected abstract void ensureCreated(string database);

    /// <summary>Refuse every connection to the tenant's database, and cut the ones already open.</summary>
    public abstract Task TakeOfflineAsync(string tenant);

    public abstract Task BringOnlineAsync(string tenant);
}

public class PostgresqlTenancyDatabases(string key) : TenancyDatabases(key)
{
    public override Dialect Dialect => Dialect.Postgresql;

    public override string ConnectionStringFor(string label) =>
        new NpgsqlConnectionStringBuilder(Servers.PostgresConnectionString) { Database = NameFor(label) }.ConnectionString;

    public override string RigConnectionStringFor(string label) =>
        new NpgsqlConnectionStringBuilder(ConnectionStringFor(label)) { ApplicationName = "durable-rig" }.ConnectionString;

    protected override void ensureCreated(string database)
    {
        using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        conn.Open();

        using var exists = new NpgsqlCommand("select count(*) from pg_database where datname = @name", conn);
        exists.Parameters.AddWithValue("name", database);
        if ((long)exists.ExecuteScalar()! == 0)
        {
            using var create = new NpgsqlCommand($"create database {database}", conn);
            create.ExecuteNonQuery();
        }

        using var online = new NpgsqlCommand($"alter database {database} with allow_connections true", conn);
        online.ExecuteNonQuery();
    }

    public override async Task TakeOfflineAsync(string tenant)
    {
        var database = NameFor(tenant);
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();

        // datallowconn = false refuses even a superuser; the open connections are cut so pooled ones fail too
        await using var cmd = new NpgsqlCommand(
            $"alter database {database} with allow_connections false; " +
            $"select pg_terminate_backend(pid) from pg_stat_activity where datname = '{database}' and pid <> pg_backend_pid();",
            conn);
        await cmd.ExecuteNonQueryAsync();
        clearRigPool(tenant);
    }

    public override async Task BringOnlineAsync(string tenant)
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"alter database {NameFor(tenant)} with allow_connections true", conn);
        await cmd.ExecuteNonQueryAsync();
        clearRigPool(tenant);
    }

    // The rig's own pool only (see RigConnectionStringFor): a pooled connection the outage cut would otherwise fail
    // the first snapshot after the database is back
    private void clearRigPool(string tenant)
    {
        using var conn = new NpgsqlConnection(RigConnectionStringFor(tenant));
        NpgsqlConnection.ClearPool(conn);
    }
}

public class SqlServerTenancyDatabases(string key) : TenancyDatabases(key)
{
    public override Dialect Dialect => Dialect.SqlServer;

    public override string ConnectionStringFor(string label) =>
        new SqlConnectionStringBuilder(Servers.SqlServerConnectionString) { InitialCatalog = NameFor(label) }.ConnectionString;

    public override string RigConnectionStringFor(string label) =>
        new SqlConnectionStringBuilder(ConnectionStringFor(label)) { ApplicationName = "durable-rig" }.ConnectionString;

    protected override void ensureCreated(string database)
    {
        using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        conn.Open();

        using var cmd = new SqlCommand(
            $"""
             if db_id('{database}') is null create database [{database}];
             if exists (select 1 from sys.databases where name = '{database}' and state_desc <> 'ONLINE')
                 alter database [{database}] set online;
             """, conn);
        cmd.ExecuteNonQuery();
    }

    public override async Task TakeOfflineAsync(string tenant)
    {
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand($"alter database [{NameFor(tenant)}] set offline with rollback immediate", conn);
        await cmd.ExecuteNonQueryAsync();
        clearRigPool(tenant);
    }

    public override async Task BringOnlineAsync(string tenant)
    {
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand($"alter database [{NameFor(tenant)}] set online", conn);
        await cmd.ExecuteNonQueryAsync();
        clearRigPool(tenant);
    }

    // The rig's own pool only (see RigConnectionStringFor)
    private void clearRigPool(string tenant)
    {
        using var conn = new SqlConnection(RigConnectionStringFor(tenant));
        SqlConnection.ClearPool(conn);
    }
}

/// <summary>
/// A store with one message store (database) per tenant. <see cref="StoreProfile.Databases" /> is the main
/// database plus one per tenant, each with its own <c>rig_ledger</c>, labelled "main" or with the tenant id, so a
/// row's <c>Database</c> says where it landed.
/// </summary>
public abstract class TenancyStoreProfile : StoreProfile
{
    private RigDatabase[]? _databases;

    protected TenancyStoreProfile(TenancyDatabases tenancy)
    {
        Tenancy = tenancy;
    }

    public TenancyDatabases Tenancy { get; }

    public IReadOnlyList<string> Tenants => TenancyDatabases.Tenants;

    /// <summary>The schema every database of this profile keeps its Wolverine tables and ledger in.</summary>
    public abstract string Schema { get; }

    public override IReadOnlyList<RigDatabase> Databases => _databases ??= TenancyDatabases.Tenants
        .Prepend(TenancyDatabases.MainLabel)
        .Select(label => new RigDatabase(label, Tenancy.Dialect, connect(Tenancy.RigConnectionStringFor(label)), Schema))
        .ToArray();

    public RigDatabase DatabaseFor(string? tenant) =>
        Databases.Single(x => x.Label == (string.IsNullOrEmpty(tenant) ? TenancyDatabases.MainLabel : tenant));

    /// <summary>The label of the database a tenant's rows belong in.</summary>
    public string LabelFor(string? tenant) => DatabaseFor(tenant).Label;

    private Func<System.Data.Common.DbConnection> connect(string connectionString) => Tenancy.Dialect == Dialect.Postgresql
        ? () => new NpgsqlConnection(connectionString)
        : () => new SqlConnection(connectionString);

    public override void ConfigureDatabaseQueue(Wolverine.WolverineOptions opts, string queueName) =>
        throw new NotSupportedException("The tenancy scenarios run over a durable local queue and RabbitMQ");
}
