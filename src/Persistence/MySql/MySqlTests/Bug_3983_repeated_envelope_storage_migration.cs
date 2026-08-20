using IntegrationTests;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Weasel.Core;
using Weasel.MySql;
using Wolverine;
using Wolverine.MySql;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;

namespace MySqlTests;

/// <summary>
///     GH-3983: MySQL implicitly creates a backing index for the wolverine_node_assignments
///     foreign key and names it after the constraint, so the schema diff saw a permanently
///     "extra" index and emitted a DROP INDEX that InnoDB refuses (error 1553). The first
///     migration against an empty database is a pure CREATE and never hit it — every start
///     after that logged an error.
/// </summary>
[Collection("mysql")]
public class Bug_3983_repeated_envelope_storage_migration
{
    private const string SchemaName = "bug_3983";

    private static MySqlMessageStore buildStore()
    {
        var dataSource = MySqlDataSourceFactory.Create(Servers.MySqlConnectionString);
        var settings = new DatabaseSettings
        {
            SchemaName = SchemaName,
            CommandQueuesEnabled = true,
            Role = MessageStoreRole.Main
        };

        return new MySqlMessageStore(settings, new DurabilitySettings(), dataSource,
            NullLogger<MySqlMessageStore>.Instance);
    }

    [Fact]
    public async Task migrating_an_already_current_database_is_a_no_op()
    {
        var store = buildStore();

        await store.Admin.MigrateAsync();

        // The second pass is the one the issue reports: nothing has changed, so it must
        // neither throw nor leave the database in a state that still reports drift.
        await store.Admin.MigrateAsync();
        await store.Admin.MigrateAsync();

        await store.AssertDatabaseMatchesConfigurationAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task the_node_assignments_foreign_key_reports_no_drift()
    {
        var store = buildStore();
        await store.Admin.MigrateAsync();

        var assignments = store.AllObjects()
            .OfType<Weasel.MySql.Tables.Table>()
            .Single(x => x.Identifier.Name == DatabaseConstants.NodeAssignmentsTableName);

        await using var conn = MySqlDataSourceFactory.Create(Servers.MySqlConnectionString)
            .CreateConnection();
        await conn.OpenAsync(TestContext.Current.CancellationToken);

        var delta = await assignments.FindDeltaAsync(conn, TestContext.Current.CancellationToken);

        delta.Difference.ShouldBe(SchemaPatchDifference.None);

        await conn.CloseAsync();
    }
}
