using IntegrationTests;
using Npgsql;
using Weasel.Postgresql;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Postgresql;
using Wolverine.Transports.SharedMemory;
using Xunit.Abstractions;

namespace SlowTests.SharedMemory;

public class leadership_compliance: LeadershipElectionCompliance
{
    public leadership_compliance(ITestOutputHelper output) : base(output)
    {
    }

    protected override async Task beforeBuildingHost()
    {
        await SharedMemoryQueueManager.ClearAllAsync();
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        await conn.DropSchemaAsync("registry");
        await conn.CloseAsync();
    }

    protected override void configureNode(WolverineOptions options)
    {
        options.Durability.DurabilityAgentEnabled = false;

        options.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "registry");
        options.UseSharedMemoryQueueing();
    }
}