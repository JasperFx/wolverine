using IntegrationTests;
using Microsoft.Data.SqlClient;
using Wolverine.ComplianceTests.Serverless;

namespace EfCoreTests;

/// <summary>
/// PR #5044 exploratory testing: ServerlessWithOutbox recovery with the efcore store, against real function instances
/// killed with SIGKILL. See <see cref="ServerlessOutboxRecoveryExploratory" />. Build the crash host first:
/// dotnet build src/Testing/ServerlessOutboxCrashHost -c Release -f net9.0
/// </summary>
public class serverless_with_outbox_recovery_exploratory_5044 : ServerlessOutboxRecoveryExploratory
{
    protected override string StoreName => "efcore";
    protected override string SchemaName => "explore5044_rec_ef";
    protected override string BusinessTable => "explore_items";

    protected override async Task<object?> ScalarAsync(string sql)
    {
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteScalarAsync();
    }
}
