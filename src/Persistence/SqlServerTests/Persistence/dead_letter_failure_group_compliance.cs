using IntegrationTests;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.SqlServer;

namespace SqlServerTests.Persistence;

public class dead_letter_failure_group_compliance : RelationalDeadLetterFailureGroupCompliance
{
    public override async Task<IHost> BuildCleanHost()
    {
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "dlq_groups");
                opts.Durability.Mode = DurabilityMode.Solo;

                // Covers the dead letter insert with the expires column
                opts.Durability.DeadLetterQueueExpirationEnabled = true;
            }).StartAsync();
    }
}
