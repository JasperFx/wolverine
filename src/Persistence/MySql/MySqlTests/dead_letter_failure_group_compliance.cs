using IntegrationTests;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.MySql;

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
}
