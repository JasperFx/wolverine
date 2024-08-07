using IntegrationTests;
using Microsoft.Data.SqlClient;
using Weasel.SqlServer;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.SqlServer;
using Xunit.Abstractions;

namespace SqlServerTests.Agents;

[Collection("sqlserver")]
public class leader_election : LeadershipElectionCompliance
{
    public leader_election(ITestOutputHelper output) : base(output)
    {
    }

    protected override void configureNode(WolverineOptions opts)
    {
        opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "registry");
    }

    protected override async Task dropSchema()
    {
        using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync();
        await conn.DropSchemaAsync("registry");
        await conn.CloseAsync();
    }
}