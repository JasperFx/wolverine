using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Weasel.Sqlite;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.Runtime.Agents;
using Wolverine.Sqlite;
using Xunit;

namespace SqliteTests.Agents;

/// <summary>
/// Regression for https://github.com/JasperFx/wolverine/issues/4668.
///
/// <para>
/// GH-3943 made the SQLite schema name a table-name prefix and routed every table reference through
/// <see cref="TablePrefixing" /> — except three statements in <c>SqliteNodePersistence</c>, which kept the
/// bare <see cref="DatabaseConstants" /> names. On a prefixed store those named tables that do not exist:
/// </para>
///
/// <list type="bullet">
/// <item><c>DeleteAsync</c> threw <c>no such table: wolverine_incoming_envelopes</c> at its third statement.
/// The two deletes ahead of it had already committed (SQLite autocommits per statement), so the node and its
/// assignments were gone while the envelopes it owned stayed pinned to a node number that no longer exists.
/// Nothing reclaims those: the recovery sweep only reads <c>owner_id = 0</c>, and the dead-node sweep only
/// releases owners absent from the node table — which this one now is.</item>
/// <item><c>FetchRecentRecordsAsync</c> and <c>DeleteOldNodeRecordsAsync</c> threw on
/// <c>wolverine_node_records</c>, while <c>PersistNodeRecord</c> wrote to the prefixed table. So a prefixed
/// store accumulated node records it could neither read back nor trim, and GH-3701's row cap never fired.</item>
/// </list>
///
/// <para>
/// Both failures were silent in the field: <c>NodeAgentController</c> catches around the shutdown
/// <c>DeleteAsync</c> and logs, so the store looked clean while the envelopes were stranded.
/// </para>
///
/// <para>
/// Every fact runs against both a prefixed store and the default <c>main</c>. The <c>main</c> row is the
/// negative control — it passed before the fix, so it is what proves these assertions are discriminating
/// on the prefix rather than on the flow.
/// </para>
/// </summary>
public class Bug_4668_node_persistence_honors_the_table_prefix : IAsyncLifetime
{
    private readonly SqliteTestDatabase _database =
        Servers.CreateDatabase(nameof(Bug_4668_node_persistence_honors_the_table_prefix));

    private readonly List<SqliteMessageStore> _stores = [];
    private readonly List<SqliteDataSource> _dataSources = [];

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var store in _stores)
        {
            await store.DisposeAsync();
        }

        foreach (var dataSource in _dataSources)
        {
            dataSource.Dispose();
        }

        _database.Dispose();
    }

    private async Task<SqliteMessageStore> storeForAsync(string schemaName)
    {
        var dataSource = new SqliteDataSource(_database.ConnectionString);
        _dataSources.Add(dataSource);

        var settings = new DatabaseSettings
        {
            ConnectionString = _database.ConnectionString,
            SchemaName = schemaName,
            Role = MessageStoreRole.Main
        };

        var store = new SqliteMessageStore(settings, new DurabilitySettings(), dataSource,
            NullLogger<SqliteMessageStore>.Instance);
        _stores.Add(store);

        await store.Admin.MigrateAsync();

        return store;
    }

    [Theory]
    [InlineData("reporting")]
    [InlineData("main")]
    public async Task deleting_a_node_releases_the_envelopes_it_owned(string schemaName)
    {
        var store = await storeForAsync(schemaName);

        var node = new WolverineNode
        {
            NodeId = Guid.NewGuid(),
            ControlUri = new Uri($"dbcontrol://{Guid.NewGuid()}"),
            AssignedNodeNumber = 5,
            Version = new Version(1, 0, 0)
        };

        await store.Nodes.PersistAsync(node, CancellationToken.None);

        var incoming = ObjectMother.Envelope();
        incoming.Status = EnvelopeStatus.Incoming;
        incoming.OwnerId = node.AssignedNodeNumber;
        await store.Inbox.StoreIncomingAsync(incoming);

        var outgoing = ObjectMother.Envelope();
        outgoing.Status = EnvelopeStatus.Outgoing;
        await store.Outbox.StoreOutgoingAsync(outgoing, node.AssignedNodeNumber);

        // Pin the arrangement, so that a release assertion below cannot pass vacuously against a table
        // that never held the row in the first place.
        (await countOwnedByAsync(schemaName, DatabaseConstants.IncomingTable, node.AssignedNodeNumber))
            .ShouldBe(1);
        (await countOwnedByAsync(schemaName, DatabaseConstants.OutgoingTable, node.AssignedNodeNumber))
            .ShouldBe(1);

        // This is the throw the issue reports. It is caught and logged by NodeAgentController in
        // production, which is why the stranding went unnoticed.
        await store.Nodes.DeleteAsync(node.NodeId, node.AssignedNodeNumber);

        (await store.Nodes.LoadAllNodesAsync(CancellationToken.None)).ShouldBeEmpty();

        // The half that actually matters: without this the envelopes stay pinned to a node number that
        // no longer exists, and no sweep will ever pick them up.
        (await countOwnedByAsync(schemaName, DatabaseConstants.IncomingTable, node.AssignedNodeNumber))
            .ShouldBe(0);
        (await countOwnedByAsync(schemaName, DatabaseConstants.OutgoingTable, node.AssignedNodeNumber))
            .ShouldBe(0);
        (await countOwnedByAsync(schemaName, DatabaseConstants.IncomingTable, 0)).ShouldBe(1);
        (await countOwnedByAsync(schemaName, DatabaseConstants.OutgoingTable, 0)).ShouldBe(1);
    }

    [Theory]
    [InlineData("reporting")]
    [InlineData("main")]
    public async Task node_records_can_be_read_back_and_trimmed(string schemaName)
    {
        var store = await storeForAsync(schemaName);

        // Written the way PersistNodeRecord writes them — through the prefix. The live write path already
        // did this correctly, which is the whole point: on a prefixed store the rows landed in a table the
        // two read paths below could not name. (Going through LogRecordsAsync itself would need the
        // durability batcher, which only a started host has.)
        await insertNodeRecordsAsync(schemaName, "First", "Second", "Third");

        (await store.Nodes.FetchRecentRecordsAsync(10)).Count.ShouldBe(3);

        // GH-3701's row cap, which silently did nothing on a prefixed store.
        await store.Nodes.DeleteOldNodeRecordsAsync(1);

        var remaining = await store.Nodes.FetchRecentRecordsAsync(10);
        remaining.Count.ShouldBe(1);
        remaining.Single().Description.ShouldBe("Third");
    }

    private async Task<long> countOwnedByAsync(string schemaName, string tableName, int ownerId)
    {
        await using var conn = new SqliteConnection(_database.ConnectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"select count(*) from {TablePrefixing.Apply(schemaName, tableName)} where {DatabaseConstants.OwnerId} = @owner";
        cmd.Parameters.AddWithValue("@owner", ownerId);

        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private async Task insertNodeRecordsAsync(string schemaName, params string[] descriptions)
    {
        await using var conn = new SqliteConnection(_database.ConnectionString);
        await conn.OpenAsync();

        foreach (var description in descriptions)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                $"insert into {TablePrefixing.Apply(schemaName, DatabaseConstants.NodeRecordTableName)} (node_number, event_name, timestamp, description) values (@number, @event, @time, @description)";
            cmd.Parameters.AddWithValue("@number", 1);
            cmd.Parameters.AddWithValue("@event", NodeRecordType.NodeStarted.ToString());
            cmd.Parameters.AddWithValue("@time", DateTimeOffset.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("@description", description);
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
