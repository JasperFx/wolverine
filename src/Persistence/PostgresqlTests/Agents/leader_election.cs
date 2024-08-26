using IntegrationTests;
using Npgsql;
using Weasel.Postgresql;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Postgresql;
using Xunit.Abstractions;

namespace PostgresqlTests.Agents;

[Collection("marten")]
public class leader_election : LeadershipElectionCompliance
{
    public leader_election(ITestOutputHelper output) : base(output)
    {
    }

    protected override void configureNode(WolverineOptions opts)
    {
        opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "registry");
    }

    protected override async Task beforeBuildingHost()
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        await conn.DropSchemaAsync("registry");
        await conn.CloseAsync();
    }
}