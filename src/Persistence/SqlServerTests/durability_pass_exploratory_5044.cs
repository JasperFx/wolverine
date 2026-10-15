using System.Data.Common;
using IntegrationTests;
using Microsoft.Data.SqlClient;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.SqlServer;

namespace SqlServerTests;

/// <summary>
/// Exploratory tests for PR #5044 (ServerlessWithOutbox) on SQL Server. See
/// <see cref="DurabilityPassExploratory5044Compliance" />
/// </summary>
public class durability_pass_exploratory_5044 : DurabilityPassExploratory5044Compliance
{
    protected override string SchemaName => "explore5044_flags_ss";

    protected override void ConfigurePersistence(WolverineOptions opts, string schemaName)
    {
        opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, schemaName);
    }

    protected override async Task KillSessionAsync(DbConnection connection)
    {
        var spid = ((SqlConnection)connection).ServerProcessId;

        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand($"KILL {spid}", conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
