using IntegrationTests;
using Npgsql;
using Weasel.Postgresql;
using Wolverine.ComplianceTests;
using Wolverine.Postgresql;
using Xunit.Abstractions;

namespace Wolverine.AzureServiceBus.Tests;

public class leader_election : LeadershipElectionCompliance
{
    public leader_election(ITestOutputHelper output) : base(output)
    {
    }

    protected override async Task beforeBuildingHost()
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        await conn.DropSchemaAsync("registry");
        await conn.CloseAsync();
    }

    protected override void configureNode(WolverineOptions opts)
    {
        opts.UseAzureServiceBusTesting().EnableWolverineControlQueues();
        opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "registry");
    }
}