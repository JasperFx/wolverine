using IntegrationTests;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Shouldly;
using Wolverine;
using Wolverine.MySql;
using Wolverine.Persistence.Durability;
using Weasel.Core;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Durability;
using Wolverine.RDBMS.Sagas;
using Wolverine.Runtime.Agents;
using Xunit;
using DbCommandBuilder = Weasel.Core.DbCommandBuilder;

namespace MySqlTests.Agents;

/// <summary>
/// GH-4246. Every string column in the MySQL node-table family used to take Weasel's default string
/// mapping, which on MySQL is <c>VARCHAR(255)</c> -- narrower than the widths the SQL Server and Oracle
/// stores have always declared for the same columns, and narrower than any of the calling code assumed.
/// A leader writing an <c>AssignmentChanged</c> record, whose description is an agent command's
/// ToString(), overflowed the column and failed the insert, which failed the AgentCommand batch behind
/// it: "Data too long for column 'description'". A non-default schema name was enough to get there.
/// </summary>
[Collection("mysql")]
public class Bug_4246_long_node_record_description : IAsyncLifetime
{
    private const string SchemaName = "node_record_description";
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

    private async Task<long> characterLengthOfAsync(string table, string column)
    {
        await using var conn = new MySqlConnection(Servers.MySqlConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "select character_maximum_length from information_schema.columns " +
            "where table_schema = @schema and table_name = @table and column_name = @column";
        cmd.Parameters.AddWithValue("schema", SchemaName);
        cmd.Parameters.AddWithValue("table", table);
        cmd.Parameters.AddWithValue("column", column);

        var raw = await cmd.ExecuteScalarAsync();
        raw.ShouldNotBeNull($"{SchemaName}.{table}.{column} was not provisioned");
        return Convert.ToInt64(raw);
    }

    /// <summary>
    /// Run the record through the same PersistNodeRecord operation the durability batcher hands to the
    /// database -- the operation that appears at the top of the stack trace on the issue -- rather than
    /// through INodeAgentPersistence, which needs a started runtime behind it to have a batcher at all.
    /// </summary>
    private async Task persistAsync(params NodeRecord[] records)
    {
        var settings = new DatabaseSettings { SchemaName = SchemaName };
        var operation = new PersistNodeRecord(settings, records);

        await using var conn = new MySqlConnection(Servers.MySqlConnectionString);
        await conn.OpenAsync();

        var builder = new DbCommandBuilder(conn);
        operation.ConfigureCommand(builder);

        await conn.ExecuteNonQueryAsync(builder);
        await conn.CloseAsync();
    }

    [Fact]
    public async Task the_description_column_is_wide_enough_for_a_node_record()
    {
        (await characterLengthOfAsync("wolverine_node_records", "description"))
            .ShouldBe(NodeRecord.DescriptionLength);
    }

    [Theory]
    // The rest of the family that used to default to 255. Every one of these holds an agent URI, a
    // connection string or a machine description -- none of which fits a 255 character budget reliably.
    [InlineData("wolverine_nodes", "uri")]
    [InlineData("wolverine_nodes", "description")]
    [InlineData("wolverine_node_assignments", "id")]
    [InlineData("wolverine_node_records", "event_name")]
    [InlineData("wolverine_agent_restrictions", "uri")]
    [InlineData("wolverine_agent_restrictions", "type")]
    public async Task the_node_family_string_columns_no_longer_default_to_255(string table, string column)
    {
        (await characterLengthOfAsync(table, column)).ShouldBe(500);
    }

    [Fact]
    public async Task write_and_read_back_the_description_that_broke_gh_4246()
    {
        // Verbatim from the report: an AssignAgent against an overridden MessageStorageSchemaName.
        const string reported =
            "AssignAgent { AgentUri = wolverinedb://mysql/localhost/servix_local/servix_local, " +
            "Destination = NodeDestination { NodeId = a8965d0c-d6f5-45b9-8b52-fb1f50dde7ba, " +
            "ControlUri = dbcontrol://a8965d0c-d6f5-45b9-8b52-fb1f50dde7ba/ }, " +
            "DestinationNodeId = a8965d0c-d6f5-45b9-8b52-fb1f50dde7ba }";

        reported.Length.ShouldBeGreaterThan(255);

        await persistAsync(new NodeRecord
        {
            NodeNumber = 5,
            RecordType = NodeRecordType.AssignmentChanged,
            Description = reported
        });

        var records = await _store.Nodes.FetchRecentRecordsAsync(10);
        records.Single().Description.ShouldBe(reported);
    }

    [Fact]
    public async Task a_description_past_the_column_width_is_truncated_rather_than_failing_the_insert()
    {
        // The column is wider now, but it is still bounded, so the write path has to clamp. Losing the
        // tail of a diagnostic row beats taking down the AgentCommand batch that produced it.
        var enormous = new string('x', NodeRecord.DescriptionLength * 4);

        await persistAsync(new NodeRecord
        {
            NodeNumber = 5,
            RecordType = NodeRecordType.AgentPaused,
            Description = enormous
        });

        var stored = (await _store.Nodes.FetchRecentRecordsAsync(10)).Single().Description;
        stored.Length.ShouldBe(NodeRecord.DescriptionLength);
        stored.ShouldEndWith("...");
    }
}
