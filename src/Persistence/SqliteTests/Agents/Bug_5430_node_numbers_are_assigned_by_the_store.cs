using JasperFx.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.Runtime.Agents;
using Weasel.Sqlite;
using Wolverine.Sqlite;

namespace SqliteTests.Agents;

/// <summary>
/// GH-5430. SQLite wrote WolverineNode's default number (1) into the node table, so every node in a Balanced
/// cluster was node 1. The store now assigns the number, and, like a sequence, it does not hand out again the
/// number of a node that was ejected: that node keeps its number when it resumes and re-registers.
/// </summary>
public class Bug_5430_node_numbers_are_assigned_by_the_store : IAsyncLifetime
{
    private readonly SqliteTestDatabase _database = Servers.CreateDatabase(nameof(Bug_5430_node_numbers_are_assigned_by_the_store));
    private SqliteMessageStore theStore = null!;

    public async ValueTask InitializeAsync()
    {
        var settings = new DatabaseSettings
        {
            ConnectionString = _database.ConnectionString,
            SchemaName = "main",
            Role = MessageStoreRole.Main
        };

        theStore = new SqliteMessageStore(settings, new DurabilitySettings(),
            new SqliteDataSource(_database.ConnectionString), NullLogger<SqliteMessageStore>.Instance);

        await theStore.Admin.MigrateAsync();
        await theStore.Admin.ClearAllAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await theStore.DisposeAsync();
        _database.Dispose();
    }

    private static WolverineNode fresh()
    {
        var id = Guid.NewGuid();
        return new WolverineNode
        {
            NodeId = id,
            ControlUri = $"dbcontrol://{id}".ToUri(),
            Version = new Version(2, 0, 1)
        };
    }

    [Fact]
    public async Task an_ejected_nodes_number_is_not_handed_out_again()
    {
        var first = fresh();
        first.AssignedNodeNumber = await theStore.Nodes.PersistAsync(first, CancellationToken.None);

        var frozen = fresh();
        frozen.AssignedNodeNumber = await theStore.Nodes.PersistAsync(frozen, CancellationToken.None);

        // Its NodeStarted record, which the node observer writes right after the node registers
        await using (var conn = new SqliteConnection(_database.ConnectionString))
        {
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            await conn.CreateCommand(
                    $"insert into {theStore.TableNameFor(DatabaseConstants.NodeRecordTableName)} (node_number, event_name) values ({frozen.AssignedNodeNumber}, '{NodeRecordType.NodeStarted}')")
                .ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        // The leader ejects the frozen node as stale
        await theStore.Nodes.DeleteAsync(frozen.NodeId, frozen.AssignedNodeNumber);

        var newcomer = await theStore.Nodes.PersistAsync(fresh(), CancellationToken.None);

        newcomer.ShouldBeGreaterThan(frozen.AssignedNodeNumber);
    }

    [Fact]
    public async Task health_checks_read_back_as_utc()
    {
        // SQLite writes datetime('now'), which is UTC text with no offset. Read back with DateTimeOffset.Parse, it
        // took the machine's offset, so west of Greenwich every health check looked hours in the future and the
        // leader never found a stale node to eject. CI runs in UTC, so this only fails on a machine that does not
        var node = fresh();
        node.AssignedNodeNumber = await theStore.Nodes.PersistAsync(node, CancellationToken.None);
        await theStore.Nodes.MarkHealthCheckAsync(node, CancellationToken.None);

        var loaded = (await theStore.Nodes.LoadAllNodesAsync(CancellationToken.None)).Single();

        loaded.LastHealthCheck.ShouldBe(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
        loaded.Started.ShouldBe(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
    }
}
