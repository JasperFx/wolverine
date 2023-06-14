using IntegrationTests;
using JasperFx.Core;
using Npgsql;
using PersistenceTests.Agents;
using Weasel.Postgresql;
using Wolverine.RDBMS;
using Wolverine.Tracking;
using Xunit;

namespace PersistenceTests.Marten.MultiTenancy;

public class agent_mechanics : MultiTenancyContext
{
    public agent_mechanics(MultiTenancyFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task all_agents_start()
    {
        await using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync();
            await conn.CreateCommand($"delete from control.{DatabaseConstants.NodeAssignmentsTableName}")
                .ExecuteNonQueryAsync();
            await conn.CreateCommand($"delete from control.{DatabaseConstants.NodeTableName}").ExecuteNonQueryAsync();
            await conn.CloseAsync();
        }


        await Fixture.RestartAsync();

        await Fixture.Host.GetRuntime().Tracker.WaitUntilAssumesLeadershipAsync(5.Seconds());

        // Should be 4 agents, one for the master db, and 3 for the tenants
        await Fixture.Host.WaitUntilAssignmentsChangeTo(w =>
        {
            w.AgentScheme = DurabilityAgent.AgentScheme;
            w.ExpectRunningAgents(Fixture.Host, 4);
        }, 30.Seconds());
    }
}