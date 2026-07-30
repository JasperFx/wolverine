using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Weasel.Sqlite;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.Runtime.Agents;
using Wolverine.Sqlite;
using Xunit;

namespace SqliteTests.Agents;

/// <summary>
/// GH-3701: the node record row cap. Sqlite inherited the interface's no-op default before this, so
/// <c>wolverine_node_records</c> was bounded only by age.
/// </summary>
public class delete_old_node_records : IAsyncLifetime
{
    private readonly SqliteTestDatabase _database = Servers.CreateDatabase(nameof(delete_old_node_records));
    private SqliteMessageStore _store = null!;
    private SqliteDataSource _dataSource = null!;

    public async ValueTask InitializeAsync()
    {
        _dataSource = new SqliteDataSource(_database.ConnectionString);

        var settings = new DatabaseSettings
        {
            ConnectionString = _database.ConnectionString,
            SchemaName = "main",
            Role = MessageStoreRole.Main
        };

        _store = new SqliteMessageStore(settings, new DurabilitySettings(), _dataSource,
            NullLogger<SqliteMessageStore>.Instance);

        await _store.Admin.MigrateAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _dataSource.Dispose();
        _database.Dispose();
    }

    private async Task insertNodeRecordsAsync(int count)
    {
        await using var conn = new SqliteConnection(_database.ConnectionString);
        await conn.OpenAsync();

        for (var i = 1; i <= count; i++)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                $"insert into {DatabaseConstants.NodeRecordTableName} (node_number, event_name, timestamp, description) values (@number, @event, @time, @description)";
            cmd.Parameters.AddWithValue("@number", 1);
            cmd.Parameters.AddWithValue("@event", NodeRecordType.AssignmentChanged.ToString());
            cmd.Parameters.AddWithValue("@time", DateTimeOffset.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("@description", $"Record {i:00}");
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
