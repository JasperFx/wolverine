using IntegrationTests;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.MySql;
using Wolverine.RDBMS;

namespace MySqlTests;

public class dead_letter_failure_group_compliance : RelationalDeadLetterFailureGroupCompliance
{
    public override async Task<IHost> BuildCleanHost()
    {
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithMySql(Servers.MySqlConnectionString, "dlq_groups");
                opts.Durability.Mode = DurabilityMode.Solo;
            }).StartAsync();
    }

    // LOCK TABLES is not transactional, so it needs its own release
    protected override Task<IAsyncDisposable?> blockDeadLetterHistoryWritesAsync() =>
        holdLockAsync(
            $"lock tables {((IMessageDatabase)thePersistence).TableNameFor(DatabaseConstants.DeadLetterHistoryTable)} read",
            "unlock tables");
}
