using IntegrationTests;
using Npgsql;
using Wolverine.ComplianceTests.Serverless;

namespace MartenTests;

/// <summary>
/// PR #5044 exploratory testing: ServerlessWithOutbox recovery with the marten store, against real function instances
/// killed with SIGKILL. See <see cref="ServerlessOutboxRecoveryExploratory" />. Build the crash host first:
/// dotnet build src/Testing/ServerlessOutboxCrashHost -c Release -f net9.0
/// </summary>
public class serverless_with_outbox_recovery_exploratory_5044 : ServerlessOutboxRecoveryExploratory
{
    protected override string StoreName => "marten";
    protected override string SchemaName => "explore5044_rec_marten";
    protected override string BusinessTable => "mt_doc_exploredoc";

    protected override async Task<object?> ScalarAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteScalarAsync();
    }
}
