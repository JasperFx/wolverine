using IntegrationTests;
using Microsoft.Extensions.Logging.Abstractions;
using Oracle.ManagedDataAccess.Client;
using Shouldly;
using Weasel.Oracle;
using Wolverine;
using Wolverine.Oracle;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Sagas;
using Wolverine.Runtime.Agents;
using Xunit;

namespace OracleTests.Agents;

/// <summary>
/// GH-3701: the node record row cap. Oracle inherited the interface's no-op default before this, so
/// <c>wolverine_node_records</c> was bounded only by age — which is no ceiling at all under assignment churn.
/// </summary>
[Collection("oracle")]
public class delete_old_node_records : IAsyncLifetime
{
    private const string SchemaName = "WOLVERINE";
    private OracleMessageStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        var dataSource = new OracleDataSource(Servers.OracleConnectionString);
        var settings = new DatabaseSettings
        {
            ConnectionString = Servers.OracleConnectionString,
            SchemaName = SchemaName,
            Role = MessageStoreRole.Main
        };

        _store = new OracleMessageStore(settings, new DurabilitySettings(), dataSource,
            NullLogger<OracleMessageStore>.Instance, Array.Empty<SagaTableDefinition>());

        // Deliberately NOT Admin.RebuildAsync(). In Oracle a schema IS a user, so every test in the
        // "oracle" collection shares WOLVERINE -- rebuilding it drops the tables out from under the
        // siblings that run after this class. Migrate to make sure the table exists, then clear only the
        // one table this class actually owns.
        await _store.Admin.MigrateAsync();
        await clearNodeRecordsAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await clearNodeRecordsAsync();
        await _store.DisposeAsync();
    }

    private static async Task clearNodeRecordsAsync()
    {
        await using var conn = new OracleConnection(Servers.OracleConnectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM {SchemaName}.{DatabaseConstants.NodeRecordTableName}";
        await cmd.ExecuteNonQueryAsync();

        await conn.CloseAsync();
    }

    private static async Task insertNodeRecordsAsync(int count)
    {
        await using var conn = new OracleConnection(Servers.OracleConnectionString);
        await conn.OpenAsync();

        for (var i = 1; i <= count; i++)
        {
            await using var cmd = conn.CreateCommand();
            cmd.BindByName = true;

            // NOT :number -- NUMBER is an Oracle reserved word and a bind variable named after one is
            // rejected with ORA-01745: invalid host/bind variable name. Same for :description, which
            // collides with the DESCRIPTION keyword in some contexts. Prefix them.
            cmd.CommandText =
                $"INSERT INTO {SchemaName}.{DatabaseConstants.NodeRecordTableName} (node_number, event_name, description) VALUES (:p_number, :p_event, :p_description)";
            cmd.Parameters.Add("p_number", 1);
            cmd.Parameters.Add("p_event", NodeRecordType.AssignmentChanged.ToString());
            cmd.Parameters.Add("p_description", $"Record {i:00}");
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
