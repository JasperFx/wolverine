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
using Xunit;

namespace MySqlTests.Agents;

/// <summary>
/// GH-3701: the node record row cap. MySQL inherited the interface's no-op default before this, so
/// <c>wolverine_node_records</c> was bounded only by age — which is no ceiling at all under assignment churn.
/// MySQL also rejects a LIMIT inside a subquery over the table being deleted, so the implementation resolves
/// a floor id first; these tests are what pin that rewrite to the same semantics as the other stores.
/// </summary>
[Collection("mysql")]
public class delete_old_node_records : IAsyncLifetime
{
    private const string SchemaName = "node_record_retention";
    private MySqlMessageStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        await using (var conn = new MySqlConnection(Servers.MySqlConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"DROP DATABASE IF EXISTS `{SchemaName}`";
            await cmd.ExecuteNonQueryAsync();
            await conn.CloseAsync();
        }

        var dataSource = MySqlDataSourceFactory.Create(Servers.MySqlConnectionString);
        var settings = new DatabaseSettings
        {
            ConnectionString = Servers.MySqlConnectionString,
            SchemaName = SchemaName,
            Role = MessageStoreRole.Main
        };

        _store = new MySqlMessageStore(settings, new DurabilitySettings(), dataSource,
            NullLogger<MySqlMessageStore>.Instance, Array.Empty<SagaTableDefinition>());

        await _store.Admin.MigrateAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
    }

    private async Task insertNodeRecordsAsync(int count)
    {
        await using var conn = new MySqlConnection(Servers.MySqlConnectionString);
        await conn.OpenAsync();

        for (var i = 1; i <= count; i++)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                $"INSERT INTO {SchemaName}.{DatabaseConstants.NodeRecordTableName} (node_number, event_name, description) VALUES (@number, @event, @description)";
            cmd.Parameters.AddWithValue("number", 1);
            cmd.Parameters.AddWithValue("event", NodeRecordType.AssignmentChanged.ToString());
            cmd.Parameters.AddWithValue("description", $"Record {i:00}");
            await cmd.ExecuteNonQueryAsync();
        }

        await conn.CloseAsync();
    }

    [Fact]
    public async Task retains_the_most_recent_records()
    {
        await insertNodeRecordsAsync(10);
        (await _store.Nodes.FetchRecentRecordsAsync(100)).Count.ShouldBe(10);

        await _store.Nodes.DeleteOldNodeRecordsAsync(3);

        var remaining = await _store.Nodes.FetchRecentRecordsAsync(100);
        remaining.Count.ShouldBe(3);
        remaining.Select(x => x.Description).OrderBy(x => x)
            .ShouldBe(["Record 08", "Record 09", "Record 10"]);
    }

    [Fact]
    public async Task zero_retain_is_a_noop()
    {
        await insertNodeRecordsAsync(3);

        await _store.Nodes.DeleteOldNodeRecordsAsync(0);

        (await _store.Nodes.FetchRecentRecordsAsync(100)).Count.ShouldBe(3);
    }

    [Fact]
    public async Task fewer_records_than_the_cap_keeps_all()
    {
        await insertNodeRecordsAsync(2);

        await _store.Nodes.DeleteOldNodeRecordsAsync(5);

        (await _store.Nodes.FetchRecentRecordsAsync(100)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task empty_table_does_not_throw()
    {
        await _store.Nodes.DeleteOldNodeRecordsAsync(5);

        (await _store.Nodes.FetchRecentRecordsAsync(100)).Count.ShouldBe(0);
    }
}
