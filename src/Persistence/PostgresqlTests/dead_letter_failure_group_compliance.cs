using IntegrationTests;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Postgresql;

namespace PostgresqlTests;

public class dead_letter_failure_group_compliance : RelationalDeadLetterFailureGroupCompliance
{
    public override async Task<IHost> BuildCleanHost()
    {
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "dlq_groups");
                opts.Durability.Mode = DurabilityMode.Solo;
            }).StartAsync();
    }
}
