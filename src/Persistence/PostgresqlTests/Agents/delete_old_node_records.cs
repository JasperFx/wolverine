using IntegrationTests;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Shouldly;
using Weasel.Postgresql;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.RDBMS;
using Wolverine.Runtime.Agents;
using Xunit;

namespace PostgresqlTests.Agents;

[Collection("marten")]
public class delete_old_node_records : IAsyncLifetime
{
    private PostgresqlMessageStore _store = null!;
    private NpgsqlDataSource _dataSource = null!;
    private readonly string SchemaName = $"node_records_{Guid.NewGuid().ToString("N")[..8]}";

    public async Task InitializeAsync()
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        await conn.DropSchemaAsync(SchemaName);
        await conn.CloseAsync();

        _dataSource = NpgsqlDataSource.Create(Servers.PostgresConnectionString);

        var settings = new DatabaseSettings
        {
            ConnectionString = Servers.PostgresConnectionString,
            SchemaName = SchemaName,
            Role = MessageStoreRole.Main
        };

        _store = new PostgresqlMessageStore(settings, new DurabilitySettings(),
            _dataSource, NullLogger<PostgresqlMessageStore>.Instance);

        await _store.Admin.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        await _dataSource.DisposeAsync();
    }

    private async Task InsertNodeRecordsAsync(int count)
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();

        for (var i = 1; i <= count; i++)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"insert into {SchemaName}.{DatabaseConstants.NodeRecordTableName} (node_number, event_name, description) values ($1, $2, $3)";
            cmd.Parameters.AddWithValue(1);
            cmd.Parameters.AddWithValue(NodeRecordType.NodeStarted.ToString());
            cmd.Parameters.AddWithValue($"Record {i}");
            await cmd.ExecuteNonQueryAsync();
        }

        await conn.CloseAsync();
    }

    [Fact]
    public async Task retains_most_recent_records()
    {
        await InsertNodeRecordsAsync(10);

        var allRecords = await _store.Nodes.FetchRecentRecordsAsync(100);
        allRecords.Count.ShouldBe(10);

        await _store.Nodes.DeleteOldNodeRecordsAsync(3);

        var remaining = await _store.Nodes.FetchRecentRecordsAsync(100);
        remaining.Count.ShouldBe(3);

        // The retained records should be the most recent ones
        foreach (var record in remaining)
        {
            record.Description.ShouldBeOneOf("Record 8", "Record 9", "Record 10");
        }
    }

    [Fact]
    public async Task zero_retain_is_noop()
    {
        await InsertNodeRecordsAsync(3);

        await _store.Nodes.DeleteOldNodeRecordsAsync(0);

        var remaining = await _store.Nodes.FetchRecentRecordsAsync(100);
        remaining.Count.ShouldBe(3);
    }

    [Fact]
    public async Task fewer_records_than_retain_count_keeps_all()
    {
        await InsertNodeRecordsAsync(2);

        await _store.Nodes.DeleteOldNodeRecordsAsync(5);

        var remaining = await _store.Nodes.FetchRecentRecordsAsync(100);
        remaining.Count.ShouldBe(2);
    }

    [Fact]
    public async Task empty_table_does_not_throw()
    {
        await _store.Nodes.DeleteOldNodeRecordsAsync(5);

        var remaining = await _store.Nodes.FetchRecentRecordsAsync(100);
        remaining.Count.ShouldBe(0);
    }
}
