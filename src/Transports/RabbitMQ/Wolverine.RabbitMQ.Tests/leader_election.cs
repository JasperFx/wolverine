using IntegrationTests;
using JasperFx.Core;
using Npgsql;
using Weasel.Postgresql;
using Wolverine.ComplianceTests;
using Wolverine.Postgresql;
using Wolverine.Runtime.Agents;
using Wolverine.Tracking;
using Xunit;
using Xunit.Abstractions;

namespace Wolverine.RabbitMQ.Tests;

public class leader_election : LeadershipElectionCompliance
{
    public leader_election(ITestOutputHelper output) : base(output)
    {
    }

    protected override void configureNode(WolverineOptions opts)
    {
        opts.UseRabbitMq().EnableWolverineControlQueues().DisableDeadLetterQueueing();
        opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "registry");

        opts.ListenToRabbitQueue("admin").ListenOnlyAtLeader();
    }

    protected override async Task beforeBuildingHost()
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        await conn.DropSchemaAsync("registry");
        await conn.CloseAsync();
    }
}