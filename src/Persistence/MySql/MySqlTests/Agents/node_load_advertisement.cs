using IntegrationTests;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Shouldly;
using Wolverine;
using Wolverine.MySql;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Sagas;
using Wolverine.Runtime.Agents;

namespace MySqlTests.Agents;

/// <summary>
/// GH-4593, the MySQL half. The load_factor column and every statement naming it are gated on
/// CapacityAwareAssignment, exactly as the PostgreSQL store has done since GH-3959, so upgrading without
/// opting in migrates nothing and reads nothing.
/// </summary>
[Collection("mysql")]
public class node_load_advertisement
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string SchemaName = "node_load_4593";

    private static async Task<MySqlMessageStore> buildStoreAsync(bool capacityAware)
    {
        await using (var conn = new MySqlConnection(Servers.MySqlConnectionString))
        {
            await conn.OpenAsync(Ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"DROP DATABASE IF EXISTS `{SchemaName}`";
            await cmd.ExecuteNonQueryAsync(Ct);
        }

        var settings = new DatabaseSettings
        {
            ConnectionString = Servers.MySqlConnectionString,
            SchemaName = SchemaName,
            Role = MessageStoreRole.Main
        };

        var database = new MySqlMessageStore(settings,
            new DurabilitySettings { CapacityAwareAssignment = capacityAware },
            MySqlDataSourceFactory.Create(Servers.MySqlConnectionString),
            NullLogger<MySqlMessageStore>.Instance, Array.Empty<SagaTableDefinition>());

        await database.Admin.MigrateAsync();

        return database;
    }

    private static async Task<int> loadFactorColumnCountAsync()
    {
        await using var conn = new MySqlConnection(Servers.MySqlConnectionString);
        await conn.OpenAsync(Ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"select count(*) from information_schema.columns where table_schema = '{SchemaName}' and table_name = 'wolverine_nodes' and column_name = 'load_factor'";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(Ct));
    }

    private static WolverineNode newNode() => new()
    {
        NodeId = Guid.NewGuid(),
        ControlUri = new Uri($"dbcontrol://{Guid.NewGuid()}"),
        Description = Environment.MachineName
    };

    [Fact]
    public async Task the_column_is_provisioned_and_round_trips_a_reading_when_the_flag_is_on()
    {
        var store = await buildStoreAsync(capacityAware: true);

        try
        {
            (await loadFactorColumnCountAsync()).ShouldBe(1);

            var node = newNode();
            node.AssignedNodeNumber = await store.Nodes.PersistAsync(node, Ct);

            node.LoadFactor = 42.5;
            (await store.Nodes.MarkHealthCheckAsync(node, Ct)).ShouldBeTrue();

            var reloaded = await store.Nodes.LoadAllNodesAsync(Ct);
            reloaded.Single(x => x.NodeId == node.NodeId).LoadFactor.ShouldBe(42.5);

            // and the re-registration path carries it too -- that is the one that runs when a peer has
            // deleted this node's row out from under it
            node.LoadFactor = 13.25;
            await store.Nodes.ReregisterNodeAsync(node, Ct);

            (await store.Nodes.LoadNodeAsync(node.NodeId, Ct))!.LoadFactor.ShouldBe(13.25);
        }
        finally
        {
            await store.DisposeAsync();
        }
    }

    [Fact]
    public async Task no_column_and_a_working_agent_plane_when_the_flag_is_off()
    {
        var store = await buildStoreAsync(capacityAware: false);

        try
        {
            var node = newNode();
            node.AssignedNodeNumber = await store.Nodes.PersistAsync(node, Ct);

            // a reading is taken but has nowhere to go, and nothing in the node plane may notice
            node.LoadFactor = 42.5;
            (await store.Nodes.MarkHealthCheckAsync(node, Ct)).ShouldBeTrue();
            await store.Nodes.ReregisterNodeAsync(node, Ct);

            var reloaded = await store.Nodes.LoadAllNodesAsync(Ct);
            reloaded.ShouldNotBeEmpty();
            reloaded.ShouldAllBe(x => x.LoadFactor == null);

            await store.Nodes.LoadNodeAgentStateAsync(Ct);
        }
        finally
        {
            await store.DisposeAsync();
        }

        (await loadFactorColumnCountAsync()).ShouldBe(0);
    }
}
