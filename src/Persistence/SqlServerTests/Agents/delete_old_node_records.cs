using IntegrationTests;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Weasel.SqlServer;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Sagas;
using Wolverine.Runtime.Agents;
using Wolverine.SqlServer.Persistence;
using Xunit;

namespace SqlServerTests.Agents;

public class delete_old_node_records : IAsyncLifetime
{
    private SqlServerMessageStore _store = null!;
    private const string SchemaName = "node_records_test";

    public async Task InitializeAsync()
    {
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync();
        await conn.DropSchemaAsync(SchemaName);

        var settings = new DatabaseSettings
        {
            ConnectionString = Servers.SqlServerConnectionString,
            SchemaName = SchemaName,
            Role = MessageStoreRole.Main
        };

        _store = new SqlServerMessageStore(settings, new DurabilitySettings(),
            NullLogger<SqlServerMessageStore>.Instance, Array.Empty<SagaTableDefinition>());

        await _store.Admin.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
    }

    private async Task InsertNodeRecordsAsync(int count)
    {
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync();

        for (var i = 1; i <= count; i++)
        {
            await conn.CreateCommand(
                    $"insert into {SchemaName}.{DatabaseConstants.NodeRecordTableName} (node_number, event_name, description) values (@node, @event, @desc)")
                .With("node", 1)
                .With("event", NodeRecordType.NodeStarted.ToString())
                .With("desc", $"Record {i}")
                .ExecuteNonQueryAsync();
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
