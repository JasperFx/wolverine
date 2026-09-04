using IntegrationTests;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Weasel.Core;
using Weasel.SqlServer;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Sagas;
using Wolverine.Runtime.Agents;
using Wolverine.SqlServer.Persistence;
using Xunit;

namespace SqlServerTests.Agents;

/// <summary>
/// GH-4246 follow-up. The SQL Server node-table family declared several of its string columns with
/// AddColumn&lt;string&gt;(), and Weasel.SqlServer maps a bare string to varchar(100). Every one of
/// those columns holds an agent URI, a listener URI or a version banner -- none of which fits inside
/// 100 characters on a real cluster. An agent URI like
/// "event-subscriptions://marten/SomeLongProjectionName@some-tenant-id" or a
/// "wolverinedb://sqlserver/host/schema/database" would fail the insert with
/// "String or binary data would be truncated". The MySQL and Oracle equivalents of the same columns
/// were widened by GH-4246; SQL Server was missed. The restrictions table is on a live write path --
/// <c>ApplyRestrictionsAsync</c> -> <c>PersistAgentRestrictionsAsync</c> -- so pinning or pausing an
/// agent with a long URI failed outright.
/// </summary>
[Collection("sqlserver")]
public class Bug_4246_sql_server_node_family_column_widths : IAsyncLifetime
{
    private const string SchemaName = "node_column_widths";
    private SqlServerMessageStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        await using (var conn = new SqlConnection(Servers.SqlServerConnectionString))
        {
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            await conn.DropSchemaAsync(SchemaName);
            await conn.CloseAsync();
        }

        var settings = new DatabaseSettings
        {
            ConnectionString = Servers.SqlServerConnectionString,
            SchemaName = SchemaName,
            Role = MessageStoreRole.Main
        };

        // The listeners table is behind an opt-in flag, so it only gets provisioned -- and only gets
        // asserted below -- when dynamic listeners are turned on.
        var durability = new DurabilitySettings { EnableDynamicListeners = true };

        _store = new SqlServerMessageStore(settings, durability,
            NullLogger<SqlServerMessageStore>.Instance, Array.Empty<SagaTableDefinition>());

        await _store.Admin.MigrateAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
    }

    private async Task<int> characterLengthOfAsync(string table, string column)
    {
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "select character_maximum_length from information_schema.columns " +
            "where table_schema = @schema and table_name = @table and column_name = @column";
        cmd.Parameters.AddWithValue("schema", SchemaName);
        cmd.Parameters.AddWithValue("table", table);
        cmd.Parameters.AddWithValue("column", column);

        var raw = await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        raw.ShouldNotBeNull($"{SchemaName}.{table}.{column} was not provisioned");
        return Convert.ToInt32(raw);
    }

    [Theory]
    // The columns that were still taking Weasel's varchar(100) default. Postgres declares these as
    // unbounded varchar and Oracle as VARCHAR2(4000); 500 is the width the rest of this family
    // already used on SQL Server.
    [InlineData("wolverine_agent_restrictions", "uri")]
    [InlineData("wolverine_agent_restrictions", "type")]
    [InlineData("wolverine_nodes", "version")]
    [InlineData("wolverine_listeners", "uri")]
    [InlineData("wolverine_control_queue", "message_type")]
    // Already correct before this fix -- pinned here so the whole family moves together.
    [InlineData("wolverine_nodes", "uri")]
    [InlineData("wolverine_node_assignments", "id")]
    [InlineData("wolverine_node_records", "event_name")]
    public async Task the_node_family_string_columns_are_500_wide(string table, string column)
    {
        (await characterLengthOfAsync(table, column)).ShouldBe(500);
    }

    [Fact]
    public async Task the_node_record_description_keeps_its_own_width()
    {
        (await characterLengthOfAsync("wolverine_node_records", "description"))
            .ShouldBe(NodeRecord.DescriptionLength);
    }

    [Fact]
    public async Task an_agent_uri_longer_than_100_characters_round_trips()
    {
        // Straight through the write path an operator pinning or pausing an agent actually takes:
        // ApplyRestrictionsAsync -> Nodes.PersistAgentRestrictionsAsync. Against varchar(100) this
        // failed with "String or binary data would be truncated" and took the restriction with it.
        var uri = new Uri("event-subscriptions://marten/SomeVeryLongProjectionNameThatARealApplicationUses" +
                          "@some-rather-long-tenant-identifier");

        uri.ToString().Length.ShouldBeGreaterThan(100);

        var restriction = new AgentRestriction(Guid.NewGuid(), uri, AgentRestrictionType.Pinned, 3);

        await _store.Nodes.PersistAgentRestrictionsAsync([restriction], TestContext.Current.CancellationToken);

        var state = await _store.Nodes.LoadNodeAgentStateAsync(TestContext.Current.CancellationToken);
        state.Restrictions.Current.Single().ShouldBe(restriction);
    }
}
