using IntegrationTests;
using MySqlConnector;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.MySql;
using Xunit.Abstractions;

namespace MySqlTests.LeaderElection;

public class leader_election : LeadershipElectionCompliance
{
    public leader_election(ITestOutputHelper output) : base(output)
    {
    }

    protected override void configureNode(WolverineOptions opts)
    {
        opts.PersistMessagesWithMySql(Servers.MySqlConnectionString, "registry");
    }

    protected override async Task beforeBuildingHost()
    {
        await using var conn = new MySqlConnection(Servers.MySqlConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DROP DATABASE IF EXISTS `registry`";
        await cmd.ExecuteNonQueryAsync();
        await conn.CloseAsync();
    }
}
