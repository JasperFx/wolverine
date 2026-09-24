using Microsoft.Data.Sqlite;
using Weasel.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.Runtime.Agents;
using Wolverine.Sqlite;

namespace SqliteTests.Agents;

/// <summary>
/// GH-4593, the SQLite half. The load_factor column and every statement naming it are gated on
/// CapacityAwareAssignment, exactly as the PostgreSQL store has done since GH-3959, so upgrading without
/// opting in migrates nothing and reads nothing.
/// </summary>
public class node_load_advertisement : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly SqliteTestDatabase theDatabase = Servers.CreateDatabase(nameof(node_load_advertisement));

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        theDatabase.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<SqliteMessageStore> buildStoreAsync(bool capacityAware)
    {
        var settings = new DatabaseSettings
        {
            ConnectionString = theDatabase.ConnectionString,
            SchemaName = "main",
            Role = MessageStoreRole.Main
        };

        var store = new SqliteMessageStore(settings,
            new DurabilitySettings { CapacityAwareAssignment = capacityAware },
            new SqliteDataSource(theDatabase.ConnectionString),
            NullLogger<SqliteMessageStore>.Instance);

        await store.Admin.MigrateAsync();

        return store;
    }

    private async Task<int> loadFactorColumnCountAsync()
    {
        await using var conn = new SqliteConnection(theDatabase.ConnectionString);
        await conn.OpenAsync(Ct);
        await using var cmd = conn.CreateCommand();

        // SQLite has no information_schema; pragma_table_info is the catalog. The node table carries the
        // schema name as a PREFIX rather than a qualifier (GH-3943) -- and the default "main" leaves the
        // name unprefixed -- so the table is looked up rather than spelled out.
        cmd.CommandText =
            "select count(*) from pragma_table_info((select name from sqlite_master where type = 'table' and name like '%wolverine_nodes')) where name = 'load_factor'";
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
