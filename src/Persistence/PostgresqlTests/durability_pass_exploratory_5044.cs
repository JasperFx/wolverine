using System.Data.Common;
using IntegrationTests;
using Npgsql;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Postgresql;

namespace PostgresqlTests;

/// <summary>
/// Exploratory tests for PR #5044 (ServerlessWithOutbox) on PostgreSQL. See
/// <see cref="DurabilityPassExploratory5044Compliance" />
/// </summary>
public class durability_pass_exploratory_5044 : DurabilityPassExploratory5044Compliance
{
    protected override string SchemaName => "explore5044_flags_pg";

    protected override void ConfigurePersistence(WolverineOptions opts, string schemaName)
    {
        opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, schemaName);
    }

    protected override async Task KillSessionAsync(DbConnection connection)
    {
        var pid = ((NpgsqlConnection)connection).ProcessID;

        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("select pg_terminate_backend(@pid)", conn);
        cmd.Parameters.AddWithValue("pid", pid);
        await cmd.ExecuteScalarAsync();
    }
}
