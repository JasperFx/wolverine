using IntegrationTests;
using Microsoft.Extensions.Logging.Abstractions;
using Oracle.ManagedDataAccess.Client;
using Shouldly;
using Wolverine;
using Wolverine.Oracle;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Sagas;
using Wolverine.Runtime.Agents;

namespace OracleTests.Agents;

/// <summary>
/// GH-4593, the Oracle half. The load_factor column and every statement naming it are gated on
/// CapacityAwareAssignment, exactly as the PostgreSQL store has done since GH-3959, so upgrading without
/// opting in migrates nothing and reads nothing.
/// </summary>
/// <remarks>
/// Oracle has no DROP SCHEMA, so each fact rebuilds the store's objects outright — which is also what
/// makes the column assertions independent of the order these run in.
/// </remarks>
[Collection("oracle")]
public class node_load_advertisement
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string SchemaName = "WOLVERINE";

    private static async Task<OracleMessageStore> buildStoreAsync(bool capacityAware)
    {
        var settings = new DatabaseSettings
        {
            ConnectionString = Servers.OracleConnectionString,
            SchemaName = SchemaName,
            Role = MessageStoreRole.Main
        };

        var database = new OracleMessageStore(settings,
            new DurabilitySettings { CapacityAwareAssignment = capacityAware },
            new OracleDataSource(Servers.OracleConnectionString),
            NullLogger<OracleMessageStore>.Instance, Array.Empty<SagaTableDefinition>());

        await database.Admin.RebuildAsync();

        return database;
    }

    private static async Task<int> loadFactorColumnCountAsync()
    {
        await using var conn = new OracleConnection(Servers.OracleConnectionString);
        await conn.OpenAsync(Ct);
        await using var cmd = conn.CreateCommand();

        // Oracle upper-cases these identifiers, and its catalog view is all_tab_columns.
        cmd.CommandText =
            $"select count(*) from all_tab_columns where owner = '{SchemaName}' and table_name = 'WOLVERINE_NODES' and column_name = 'LOAD_FACTOR'";
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

            // BINARY_DOUBLE rather than NUMBER, so this is an exact IEEE round trip and not a decimal
            // that has to be re-scaled on the way back.
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
            (await loadFactorColumnCountAsync()).ShouldBe(0);

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
    }
}
